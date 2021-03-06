// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CSharp.Emit;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Instrumentation;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class MethodCompiler : CSharpSymbolVisitor<TypeCompilationState, object>
    {
        private readonly CSharpCompilation compilation;
        private readonly bool generateDebugInfo;
        private readonly CancellationToken cancellationToken;
        private readonly DiagnosticBag diagnostics;
        private readonly bool hasDeclarationErrors;
        private readonly NamespaceScopeBuilder namespaceScopeBuilder;
        private readonly PEModuleBuilder moduleBeingBuiltOpt; // Null if compiling for diagnostics
        private readonly Predicate<Symbol> filterOpt;         // If not null, limit analysis to specific symbols
        private readonly DebugDocumentProvider debugDocumentProvider;

        //
        // MethodCompiler employs concurrency by following flattened fork/join pattern.
        //
        // For every item that we want to compile in parallel a new task is forked.
        // compileTaskQueue is used to track and observe all the tasks. 
        // Once compileTaskQueue is empty, we know that there are no more tasks (and no more can be created)
        // and that means we are done compiling. WaitForWorkers ensures this condition.
        //
        // Note that while tasks may fork more tasks (nested types, lambdas, whatever else that may introduce more types),
        // we do not want any child/parent relationship between spawned tasks and their creators. 
        // Creator has no real dependencies on the completion of its children and should finish and release any resources
        // as soon as it can regardless of the tasks it may have spawned.
        //
        // Stack is used so that the wait would observe the most recently added task and have 
        // more chances to do inlined execution.
        private ConcurrentStack<Task> compilerTasks;

        // This field tracks whether any bound method body had hasErrors set or whether any constant field had a bad value.
        // We track it so that we can abort emission in the event that an error occurs without a corresponding diagnostic
        // (e.g. if this module depends on a bad type or constant from another module).
        // CONSIDER: instead of storing a flag, we could track the first member symbol with an error (to improve the diagnostic).

        // NOTE: once the flag is set to true, it should never go back to false!!!
        // Do not use this as a shortcircuiting for stages that might produce diagnostics.
        // That would make diagnostics to depend on the random order in which methods are compiled.
        private bool globalHasErrors;

        private void SetGlobalErrorIfTrue(bool arg)
        {
            //NOTE: this is not a volatile write
            //      for correctness we need only single threaded consistency.
            //      Within a single task - if we have got an error it may not be safe to continue with some lowerings.
            //      It is ok if other tasks will see the change after some delay or does not observe at all.
            //      Such races are unavoidable and will just result in performing some work that is safe to do
            //      but may no longer be needed.
            //      The final Join of compiling tasks cannot happen without interlocked operations and that 
            //      will ensure that any write of the flag is globally visible.
            if (arg)
            {
                globalHasErrors = true;
            }
        }

        // Internal for testing only.
        internal MethodCompiler(CSharpCompilation compilation, PEModuleBuilder moduleBeingBuiltOpt, bool generateDebugInfo, bool hasDeclarationErrors,
            DiagnosticBag diagnostics, Predicate<Symbol> filterOpt, CancellationToken cancellationToken)
        {
            Debug.Assert(compilation != null);
            Debug.Assert(diagnostics != null);

            this.compilation = compilation;
            this.moduleBeingBuiltOpt = moduleBeingBuiltOpt;
            this.generateDebugInfo = generateDebugInfo;
            this.cancellationToken = cancellationToken;
            this.diagnostics = diagnostics;
            this.filterOpt = filterOpt;

            this.hasDeclarationErrors = hasDeclarationErrors;
            SetGlobalErrorIfTrue(hasDeclarationErrors);

            if (generateDebugInfo)
            {
                this.debugDocumentProvider = (path, basePath) => moduleBeingBuiltOpt.GetOrAddDebugDocument(path, basePath, CreateDebugDocumentForFile);
                this.namespaceScopeBuilder = new NamespaceScopeBuilder(compilation);
            }
        }

        public static void CompileMethodBodies(
            CSharpCompilation compilation,
            PEModuleBuilder moduleBeingBuiltOpt,
            bool generateDebugInfo,
            bool hasDeclarationErrors,
            DiagnosticBag diagnostics,
            Predicate<Symbol> filterOpt,
            CancellationToken cancellationToken)
        {
            Debug.Assert(compilation != null);
            Debug.Assert(diagnostics != null);

            using (Logger.LogBlock(FunctionId.CSharp_Compiler_CompileMethodBodies, message: compilation.AssemblyName, cancellationToken: cancellationToken))
            {
                if (compilation.PreviousSubmission != null)
                {
                    // In case there is a previous submission, we should ensure 
                    // it has already created anonymous type/delegates templates

                    // NOTE: if there are any errors, we will pick up what was created anyway
                    compilation.PreviousSubmission.EnsureAnonymousTypeTemplates(cancellationToken);

                    // TODO: revise to use a loop instead of a recursion
                }

                MethodCompiler methodCompiler = new MethodCompiler(
                    compilation,
                    moduleBeingBuiltOpt,
                    generateDebugInfo,
                    hasDeclarationErrors,
                    diagnostics,
                    filterOpt,
                    cancellationToken);

                if (compilation.Options.ConcurrentBuild)
                {
                    methodCompiler.compilerTasks = new ConcurrentStack<Task>();
                }

                // directly traverse global namespace (no point to defer this to async)
                methodCompiler.CompileNamespace(compilation.SourceModule.GlobalNamespace);
                methodCompiler.WaitForWorkers();

                // compile additional and anonymous types if any
                if (moduleBeingBuiltOpt != null)
                {
                    var additionalTypes = moduleBeingBuiltOpt.GetAdditionalTopLevelTypes();
                    if (!additionalTypes.IsEmpty)
                    {
                        methodCompiler.CompileSynthesizedMethods(additionalTypes, diagnostics);
                    }

                    // By this time we have processed all types reachable from module's global namespace
                    compilation.AnonymousTypeManager.AssignTemplatesNamesAndCompile(methodCompiler, moduleBeingBuiltOpt, diagnostics);
                    methodCompiler.WaitForWorkers();

                    var privateImplClass = moduleBeingBuiltOpt.PrivateImplClass;
                    if (privateImplClass != null)
                    {
                        // all threads that were adding methods must be finished now, we can freeze the class:
                        privateImplClass.Freeze();

                        methodCompiler.CompileSynthesizedMethods(privateImplClass, diagnostics);
                    }
                }

                // If we are trying to emit and there's an error without a corresponding diagnostic (e.g. because
                // we depend on an invalid type or constant from another module), then explicitly add a diagnostic.
                // This diagnostic is not very helpful to the user, but it will prevent us from emitting an invalid
                // module or crashing.
                if (moduleBeingBuiltOpt != null && methodCompiler.globalHasErrors && !diagnostics.HasAnyErrors() && !hasDeclarationErrors)
                {
                    diagnostics.Add(ErrorCode.ERR_ModuleEmitFailure, NoLocation.Singleton, ((Cci.INamedEntity)moduleBeingBuiltOpt).Name);
                }

                diagnostics.AddRange(compilation.AdditionalCodegenWarnings);

                // we can get unused field warnings only if compiling whole compilation.
                if (filterOpt == null)
                {
                    WarnUnusedFields(compilation, diagnostics, cancellationToken);
                }

                MethodSymbol entryPoint = GetEntryPoint(compilation, moduleBeingBuiltOpt, hasDeclarationErrors, diagnostics, cancellationToken);
                if (moduleBeingBuiltOpt != null)
                {
                    moduleBeingBuiltOpt.SetEntryPoint(entryPoint);
                }
            }
        }

        internal static MethodSymbol GetEntryPoint(CSharpCompilation compilation, PEModuleBuilder moduleBeingBuilt, bool hasDeclarationErrors, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            CSharpCompilationOptions options = compilation.Options;
            if (!options.OutputKind.IsApplication())
            {
                Debug.Assert(compilation.GetEntryPointAndDiagnostics(default(CancellationToken)) == null);
                return compilation.IsSubmission
                    ? DefineScriptEntryPoint(compilation, moduleBeingBuilt, compilation.GetSubmissionReturnType(), hasDeclarationErrors, diagnostics)
                    : null;
            }

            Debug.Assert(!compilation.IsSubmission);

            CSharpCompilation.EntryPoint entryPoint = compilation.GetEntryPointAndDiagnostics(cancellationToken);
            Debug.Assert(entryPoint != null);
            Debug.Assert(!entryPoint.Diagnostics.IsDefault);

            diagnostics.AddRange(entryPoint.Diagnostics);

            if ((object)compilation.ScriptClass != null)
            {
                Debug.Assert((object)entryPoint.MethodSymbol == null);
                return DefineScriptEntryPoint(compilation, moduleBeingBuilt, compilation.GetSpecialType(SpecialType.System_Void), hasDeclarationErrors, diagnostics);
            }

            Debug.Assert((object)entryPoint.MethodSymbol != null || entryPoint.Diagnostics.HasAnyErrors() || !compilation.Options.Errors.IsDefaultOrEmpty);
            return entryPoint.MethodSymbol;
        }

        internal static MethodSymbol DefineScriptEntryPoint(CSharpCompilation compilation, PEModuleBuilder moduleBeingBuilt, TypeSymbol returnType, bool hasDeclarationErrors, DiagnosticBag diagnostics)
        {
            var scriptEntryPoint = new SynthesizedEntryPointSymbol(compilation.ScriptClass, returnType, diagnostics);
            if (moduleBeingBuilt != null && !hasDeclarationErrors && !diagnostics.HasAnyErrors())
            {
                var body = scriptEntryPoint.CreateBody();

                var emittedBody = GenerateMethodBody(
                    moduleBeingBuilt,
                    scriptEntryPoint,
                    body,
                    stateMachineTypeOpt: null,
                    variableSlotAllocatorOpt: null,
                    diagnostics: diagnostics,
                    debugDocumentProvider: null,
                    namespaceScopes: default(ImmutableArray<Cci.NamespaceScope>));

                moduleBeingBuilt.SetMethodBody(scriptEntryPoint, emittedBody);
                moduleBeingBuilt.AddSynthesizedDefinition(compilation.ScriptClass, scriptEntryPoint);
            }

            return scriptEntryPoint;
        }

        private void WaitForWorkers()
        {
            var tasks = this.compilerTasks;
            if (tasks == null)
            {
                return;
            }

            Task curTask;
            while (tasks.TryPop(out curTask))
            {
                curTask.GetAwaiter().GetResult();
            }
        }

        private static void WarnUnusedFields(CSharpCompilation compilation, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            SourceAssemblySymbol assembly = (SourceAssemblySymbol)compilation.Assembly;
            diagnostics.AddRange(assembly.GetUnusedFieldWarnings(cancellationToken));
        }

        public override object VisitNamespace(NamespaceSymbol symbol, TypeCompilationState arg)
        {
            if (!PassesFilter(this.filterOpt, symbol))
            {
                return null;
            }

            arg = null; // do not use compilation state of outer type.
            cancellationToken.ThrowIfCancellationRequested();

            if (compilation.Options.ConcurrentBuild)
            {
                Task worker = CompileNamespaceAsTask(symbol);
                compilerTasks.Push(worker);
            }
            else
            {
                CompileNamespace(symbol);
            }

            return null;
        }

        private Task CompileNamespaceAsTask(NamespaceSymbol symbol)
        {
            return Task.Run(() =>
                {
                    try
                    {
                        CompileNamespace(symbol);
                        return (object)null;
                    }
                    catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                    {
                        throw ExceptionUtilities.Unreachable;
                    }
                }, this.cancellationToken);
        }

        private void CompileNamespace(NamespaceSymbol symbol)
        {
            foreach (var s in symbol.GetMembersUnordered())
            {
                s.Accept(this, null);
            }
        }

        public override object VisitNamedType(NamedTypeSymbol symbol, TypeCompilationState arg)
        {
            if (!PassesFilter(this.filterOpt, symbol))
            {
                return null;
            }

            arg = null; // do not use compilation state of outer type.
            cancellationToken.ThrowIfCancellationRequested();

            if (compilation.Options.ConcurrentBuild)
            {
                Task worker = CompileNamedTypeAsTask(symbol);
                compilerTasks.Push(worker);
            }
            else
            {
                CompileNamedType(symbol);
            }

            return null;
        }

        private Task CompileNamedTypeAsTask(NamedTypeSymbol symbol)
        {
            return Task.Run(() =>
                {
                    try
                    {
                        CompileNamedType(symbol);
                        return (object)null;
                    }
                    catch (Exception e) when (FatalError.Report(e))
                    {
                        throw ExceptionUtilities.Unreachable;
                    }
                }, this.cancellationToken);
        }

        private void CompileNamedType(NamedTypeSymbol symbol)
        {
            TypeCompilationState compilationState = new TypeCompilationState(symbol, compilation, moduleBeingBuiltOpt);

            cancellationToken.ThrowIfCancellationRequested();

            // Find the constructor of a script class.
            MethodSymbol scriptCtor = null;
            if (symbol.IsScriptClass)
            {
                // The field initializers of a script class could be arbitrary statements,
                // including blocks.  Field initializers containing blocks need to
                // use a MethodBodySemanticModel to build up the appropriate tree of binders, and
                // MethodBodySemanticModel requires an "owning" method.  That's why we're digging out
                // the constructor - it will own the field initializers.
                scriptCtor = symbol.InstanceConstructors[0];
                Debug.Assert((object)scriptCtor != null);
            }

            var synthesizedSubmissionFields = symbol.IsSubmissionClass ? new SynthesizedSubmissionFields(compilation, symbol) : null;
            var processedStaticInitializers = new Binder.ProcessedFieldInitializers();
            var processedInstanceInitializers = new Binder.ProcessedFieldInitializers();

            var sourceTypeSymbol = symbol as SourceMemberContainerTypeSymbol;

            if ((object)sourceTypeSymbol != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Binder.BindFieldInitializers(sourceTypeSymbol, scriptCtor, sourceTypeSymbol.StaticInitializers, generateDebugInfo, diagnostics, ref processedStaticInitializers);

                cancellationToken.ThrowIfCancellationRequested();
                Binder.BindFieldInitializers(sourceTypeSymbol, scriptCtor, sourceTypeSymbol.InstanceInitializers, generateDebugInfo, diagnostics, ref processedInstanceInitializers);

                if (compilationState.Emitting)
                {
                    CompileSynthesizedExplicitImplementations(sourceTypeSymbol, compilationState);
                }
            }

            // Indicates if a static constructor is in the member,
            // so we can decide to synthesize a static constructor.
            bool hasStaticConstructor = false;

            foreach (var member in symbol.GetMembers())
            {
                //When a filter is supplied, limit the compilation of members passing the filter.
                if (!PassesFilter(this.filterOpt, member))
                {
                    continue;
                }

                switch (member.Kind)
                {
                    case SymbolKind.NamedType:
                        member.Accept(this, compilationState);
                        break;

                    case SymbolKind.Method:
                        {
                            MethodSymbol method = (MethodSymbol)member;
                            if (method.IsSubmissionConstructor || IsFieldLikeEventAccessor(method))
                            {
                                continue;
                            }

                                if (method.IsPartialDefinition())
                                {
                                method = method.PartialImplementationPart;
                                if ((object)method == null)
                                {
                                    continue;
                                }
                            }

                            Binder.ProcessedFieldInitializers processedInitializers =
                                method.MethodKind == MethodKind.Constructor ? processedInstanceInitializers :
                                method.MethodKind == MethodKind.StaticConstructor ? processedStaticInitializers :
                                default(Binder.ProcessedFieldInitializers);

                            CompileMethod(method, ref processedInitializers, synthesizedSubmissionFields, compilationState);

                            // Set a flag to indicate that a static constructor is created.
                            if (method.MethodKind == MethodKind.StaticConstructor)
                            {
                                hasStaticConstructor = true;
                            }

                            break;
                        }

                    case SymbolKind.Property:
                        {
                            SourcePropertySymbol sourceProperty = member as SourcePropertySymbol;
                            if ((object)sourceProperty != null && sourceProperty.IsSealed && compilationState.Emitting)
                            {
                                CompileSynthesizedSealedAccessors(sourceProperty, compilationState);
                            }
                            break;
                        }

                    case SymbolKind.Event:
                        {
                            SourceEventSymbol eventSymbol = member as SourceEventSymbol;
                            if ((object)eventSymbol != null && eventSymbol.HasAssociatedField && !eventSymbol.IsAbstract && compilationState.Emitting)
                            {
                                CompileFieldLikeEventAccessor(eventSymbol, isAddMethod: true);
                                CompileFieldLikeEventAccessor(eventSymbol, isAddMethod: false);
                            }
                            break;
                        }

                    case SymbolKind.Field:
                        {
                            SourceMemberFieldSymbol fieldSymbol = member as SourceMemberFieldSymbol;
                            if ((object)fieldSymbol != null)
                            {
                                if (fieldSymbol.IsConst)
                                {
                                    // We check specifically for constant fields with bad values because they never result
                                    // in bound nodes being inserted into method bodies (in which case, they would be covered
                                    // by the method-level check).
                                    ConstantValue constantValue = fieldSymbol.GetConstantValue(ConstantFieldsInProgress.Empty, earlyDecodingWellKnownAttributes: false);
                                    SetGlobalErrorIfTrue(constantValue == null || constantValue.IsBad);
                                }

                                if (fieldSymbol.IsFixed && compilationState.Emitting)
                                {
                                    // force the generation of implementation types for fixed-size buffers
                                    TypeSymbol discarded = fieldSymbol.FixedImplementationType(compilationState.ModuleBuilderOpt);
                                }
                            }
                            break;
                        }
                }
            }

            Debug.Assert(symbol.TypeKind != TypeKind.Submission || ((object)scriptCtor != null && scriptCtor.IsSubmissionConstructor));

            //  process additional anonymous type members
            if (AnonymousTypeManager.IsAnonymousTypeTemplate(symbol))
            {
                var processedInitializers = default(Binder.ProcessedFieldInitializers);
                foreach (var method in AnonymousTypeManager.GetAnonymousTypeHiddenMethods(symbol))
                {
                    CompileMethod(method, ref processedInitializers, synthesizedSubmissionFields, compilationState);
                }
            }

            // In the case there are field initializers but we haven't created an implicit static constructor (.cctor) for it,
            // (since we may not add .cctor implicitly created for decimals into the symbol table)
            // it is necessary for the compiler to generate the static constructor here if we are emitting.
            if (moduleBeingBuiltOpt != null && !hasStaticConstructor && !processedStaticInitializers.BoundInitializers.IsDefaultOrEmpty)
            {
                Debug.Assert(processedStaticInitializers.BoundInitializers.All((init) =>
                    (init.Kind == BoundKind.FieldInitializer) && !((BoundFieldInitializer)init).Field.IsMetadataConstant));

                MethodSymbol method = new SynthesizedStaticConstructor(sourceTypeSymbol);
                if (PassesFilter(this.filterOpt, method))
                {
                    CompileMethod(method, ref processedStaticInitializers, synthesizedSubmissionFields, compilationState);
                    // If this method has been successfully built, we emit it.
                    if (moduleBeingBuiltOpt.GetMethodBody(method) != null)
                        moduleBeingBuiltOpt.AddSynthesizedDefinition(sourceTypeSymbol, method);
                }
            }

            // compile submission constructor last so that synthesized submission fields are collected from all script methods:
            if (synthesizedSubmissionFields != null && compilationState.Emitting)
            {
                Debug.Assert(scriptCtor.IsSubmissionConstructor);
                CompileMethod(scriptCtor, ref processedInstanceInitializers, synthesizedSubmissionFields, compilationState);
                synthesizedSubmissionFields.AddToType(scriptCtor.ContainingType, compilationState.ModuleBuilderOpt);
            }

            //  Emit synthesized methods produced during lowering if any
            if (moduleBeingBuiltOpt != null)
            {
                CompileSynthesizedMethods(compilationState);
            }

            compilationState.Free();
        }

        private void CompileSynthesizedMethods(PrivateImplementationDetails privateImplClass, DiagnosticBag diagnostics)
        {
            Debug.Assert(moduleBeingBuiltOpt != null);

            TypeCompilationState compilationState = new TypeCompilationState(null, compilation, moduleBeingBuiltOpt);
            foreach (MethodSymbol method in privateImplClass.GetMethods(new EmitContext(moduleBeingBuiltOpt, null, diagnostics)))
            {
                Debug.Assert(method.SynthesizesLoweredBoundBody);
                method.GenerateMethodBody(compilationState, diagnostics);
            }

            CompileSynthesizedMethods(compilationState);
            compilationState.Free();
        }

        private void CompileSynthesizedMethods(ImmutableArray<NamedTypeSymbol> additionalTypes, DiagnosticBag diagnostics)
        {
            foreach (var additionalType in additionalTypes)
            {
                TypeCompilationState compilationState = new TypeCompilationState(additionalType, compilation, moduleBeingBuiltOpt);
                foreach (var method in additionalType.GetMethodsToEmit())
                {
                    method.GenerateMethodBody(compilationState, diagnostics);
                }

                if (!diagnostics.HasAnyErrors())
                {
                    CompileSynthesizedMethods(compilationState);
                }

                compilationState.Free();
            }
        }

        private void CompileSynthesizedMethods(TypeCompilationState compilationState)
        {
            Debug.Assert(moduleBeingBuiltOpt != null);

            if (!compilationState.HasSynthesizedMethods)
            {
                return;
            }

            foreach (var methodWithBody in compilationState.SynthesizedMethods)
            {
                var method = methodWithBody.Method;
                var variableSlotAllocatorOpt = moduleBeingBuiltOpt.TryCreateVariableSlotAllocator(method);

                // We make sure that an asynchronous mutation to the diagnostic bag does not 
                // confuse the method body generator by making a fresh bag and then loading
                // any diagnostics emitted into it back into the main diagnostic bag.
                var diagnosticsThisMethod = DiagnosticBag.GetInstance();

                AsyncStateMachine stateMachineType;
                BoundStatement bodyWithoutAsync = AsyncRewriter.Rewrite(methodWithBody.Body, method, variableSlotAllocatorOpt, compilationState, diagnosticsThisMethod, out stateMachineType);

                MethodBody emittedBody = null;
                if (!diagnosticsThisMethod.HasAnyErrors() && !globalHasErrors)
                {
                    emittedBody = GenerateMethodBody(
                        moduleBeingBuiltOpt,
                        method,
                        bodyWithoutAsync,
                        stateMachineType,
                        variableSlotAllocatorOpt,
                        diagnosticsThisMethod,
                        debugDocumentProvider,
                        GetNamespaceScopes(method, methodWithBody.DebugImports));
                }

                this.diagnostics.AddRange(diagnosticsThisMethod);
                diagnosticsThisMethod.Free();

                // error while generating IL
                if (emittedBody == null)
                {
                    break;
                }

                moduleBeingBuiltOpt.SetMethodBody(method, emittedBody);
            }
        }

        private ImmutableArray<Cci.NamespaceScope> GetNamespaceScopes(MethodSymbol method, ConsList<Imports> debugImports)
        {
            Debug.Assert(generateDebugInfo == (namespaceScopeBuilder != null));

            return (generateDebugInfo && method.GenerateDebugInfo) ?
                namespaceScopeBuilder.GetNamespaceScopes(debugImports) : default(ImmutableArray<Cci.NamespaceScope>);
        }

        private static bool IsFieldLikeEventAccessor(MethodSymbol method)
        {
            Symbol associatedPropertyOrEvent = method.AssociatedSymbol;
            return (object)associatedPropertyOrEvent != null &&
                associatedPropertyOrEvent.Kind == SymbolKind.Event &&
                ((EventSymbol)associatedPropertyOrEvent).HasAssociatedField;
        }

        /// <summary>
        /// In some circumstances (e.g. implicit implementation of an interface method by a non-virtual method in a 
        /// base type from another assembly) it is necessary for the compiler to generate explicit implementations for
        /// some interface methods.  They don't go in the symbol table, but if we are emitting, then we should
        /// generate code for them.
        /// </summary>
        private void CompileSynthesizedExplicitImplementations(SourceMemberContainerTypeSymbol sourceTypeSymbol, TypeCompilationState compilationState)
        {
            // we are not generating any observable diagnostics here so it is ok to shortcircuit on global errors.
            if (!globalHasErrors)
            {
                foreach (var synthesizedExplicitImpl in sourceTypeSymbol.GetSynthesizedExplicitImplementations(cancellationToken))
                {
                    Debug.Assert(synthesizedExplicitImpl.SynthesizesLoweredBoundBody);
                    var discardedDiagnostics = DiagnosticBag.GetInstance();
                    synthesizedExplicitImpl.GenerateMethodBody(compilationState, discardedDiagnostics);
                    Debug.Assert(!discardedDiagnostics.HasAnyErrors());
                    discardedDiagnostics.Free();
                    moduleBeingBuiltOpt.AddSynthesizedDefinition(sourceTypeSymbol, synthesizedExplicitImpl);
                }
            }
        }

        private void CompileSynthesizedSealedAccessors(SourcePropertySymbol sourceProperty, TypeCompilationState compilationState)
        {
            SynthesizedSealedPropertyAccessor synthesizedAccessor = sourceProperty.SynthesizedSealedAccessorOpt;

            // we are not generating any observable diagnostics here so it is ok to shortcircuit on global errors.
            if ((object)synthesizedAccessor != null && !globalHasErrors)
            {
                Debug.Assert(synthesizedAccessor.SynthesizesLoweredBoundBody);
                var discardedDiagnostics = DiagnosticBag.GetInstance();
                synthesizedAccessor.GenerateMethodBody(compilationState, discardedDiagnostics);
                Debug.Assert(!discardedDiagnostics.HasAnyErrors());
                discardedDiagnostics.Free();

                moduleBeingBuiltOpt.AddSynthesizedDefinition(sourceProperty.ContainingType, synthesizedAccessor);
            }
        }

        private void CompileFieldLikeEventAccessor(SourceEventSymbol eventSymbol, bool isAddMethod)
        {
            MethodSymbol accessor = isAddMethod ? eventSymbol.AddMethod : eventSymbol.RemoveMethod;

            var diagnosticsThisMethod = DiagnosticBag.GetInstance();
            try
            {
                BoundBlock boundBody = MethodBodySynthesizer.ConstructFieldLikeEventAccessorBody(eventSymbol, isAddMethod, compilation, diagnosticsThisMethod);
                var hasErrors = diagnosticsThisMethod.HasAnyErrors();
                SetGlobalErrorIfTrue(hasErrors);

                // we cannot rely on GlobalHasErrors since that can be changed concurrently by other methods compiling
                // we however do not want to continue with generating method body if we have errors in this particular method - generating may crash
                // or if had declaration errors - we will fail anyways, but if some types are bad enough, generating may produce duplicate errors about that.
                if (!hasErrors && !hasDeclarationErrors)
                {
                    MethodBody emittedBody = GenerateMethodBody(
                        moduleBeingBuiltOpt,
                        accessor,
                        boundBody,
                        stateMachineTypeOpt: null,
                        variableSlotAllocatorOpt: null,
                        diagnostics: diagnosticsThisMethod,
                        debugDocumentProvider: debugDocumentProvider,
                        namespaceScopes: default(ImmutableArray<Cci.NamespaceScope>));

                    moduleBeingBuiltOpt.SetMethodBody(accessor, emittedBody);
                    // Definition is already in the symbol table, so don't call moduleBeingBuilt.AddCompilerGeneratedDefinition
                }
            }
            finally
            {
                diagnostics.AddRange(diagnosticsThisMethod);
                diagnosticsThisMethod.Free();
            }
        }

        public override object VisitMethod(MethodSymbol symbol, TypeCompilationState arg)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override object VisitProperty(PropertySymbol symbol, TypeCompilationState argument)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override object VisitEvent(EventSymbol symbol, TypeCompilationState argument)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override object VisitField(FieldSymbol symbol, TypeCompilationState argument)
        {
            throw ExceptionUtilities.Unreachable;
        }

        private void CompileMethod(
            MethodSymbol methodSymbol,
            ref Binder.ProcessedFieldInitializers processedInitializers,
            SynthesizedSubmissionFields previousSubmissionFields,
            TypeCompilationState compilationState)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceMethodSymbol sourceMethod = methodSymbol as SourceMethodSymbol;

            if (methodSymbol.IsAbstract)
            {
                if ((object)sourceMethod != null)
                {
                    bool diagsWritten;
                    sourceMethod.SetDiagnostics(ImmutableArray<Diagnostic>.Empty, out diagsWritten);
                    if (diagsWritten && !methodSymbol.IsImplicitlyDeclared && compilation.EventQueue != null)
                    {
                        compilation.SymbolDeclaredEvent(methodSymbol);
                    }
                }

                return;
            }

            // get cached diagnostics if not building and we have 'em
            if (moduleBeingBuiltOpt == null && (object)sourceMethod != null)
            {
                var cachedDiagnostics = sourceMethod.Diagnostics;

                if (!cachedDiagnostics.IsDefault)
                {
                    this.diagnostics.AddRange(cachedDiagnostics);
                    return;
                }
            }

            ConsList<Imports> oldDebugImports = compilationState.CurrentDebugImports;

            // In order to avoid generating code for methods with errors, we create a diagnostic bag just for this method.
            DiagnosticBag diagsForCurrentMethod = DiagnosticBag.GetInstance();

            try
            {
                bool includeInitializersInBody;
                BoundBlock body;

                // if synthesized method returns its body in lowered form
                if (methodSymbol.SynthesizesLoweredBoundBody)
                {
                    if (moduleBeingBuiltOpt != null)
                    {
                        methodSymbol.GenerateMethodBody(compilationState, diagsForCurrentMethod);
                        this.diagnostics.AddRange(diagsForCurrentMethod);
                    }

                    return;
                }

                // no need to emit the default ctor, we are not emitting those
                if (methodSymbol.IsDefaultValueTypeConstructor())
                {
                    return;
                }

                // initializers that have been analyzed but not yet lowered.
                BoundStatementList analyzedInitializers = null;

                ConsList<Imports> debugImports;

                if (methodSymbol.IsScriptConstructor)
                {
                    // rewrite top-level statements and script variable declarations to a list of statements and assignments, respectively:
                    BoundTypeOrInstanceInitializers initializerStatements = InitializerRewriter.Rewrite(processedInitializers.BoundInitializers, methodSymbol);

                    // the lowered script initializers should not be treated as initializers anymore but as a method body:
                    body = new BoundBlock(initializerStatements.Syntax, ImmutableArray<LocalSymbol>.Empty, initializerStatements.Statements) { WasCompilerGenerated = true };
                    includeInitializersInBody = false;

                    debugImports = null;
                }
                else
                {
                    // Do not emit initializers if we are invoking another constructor of this class.

                    SourceMemberContainerTypeSymbol container = methodSymbol.ContainingType as SourceMemberContainerTypeSymbol;
                    includeInitializersInBody = !processedInitializers.BoundInitializers.IsDefaultOrEmpty && 
                                                !HasThisConstructorInitializer(methodSymbol);

                    body = BindMethodBody(methodSymbol, compilationState, diagsForCurrentMethod, this.generateDebugInfo, out debugImports);

                    // lower initializers just once. the lowered tree will be reused when emitting all constructors 
                    // with field initializers. Once lowered, these initializers will be stashed in processedInitializers.LoweredInitializers
                    // (see later in this method). Don't bother lowering _now_ if this particular ctor won't have the initializers 
                    // appended to its body.
                    if (includeInitializersInBody && processedInitializers.LoweredInitializers == null)
                    {
                        analyzedInitializers = InitializerRewriter.Rewrite(processedInitializers.BoundInitializers, methodSymbol);
                        processedInitializers.HasErrors = processedInitializers.HasErrors || analyzedInitializers.HasAnyErrors;

                        if (body != null && container.IsStructType() && !methodSymbol.IsImplicitConstructor)
                        {
                            // In order to get correct diagnostics, we need to analyze initializers and the body together.
                            body = body.Update(body.Locals, body.Statements.Insert(0, analyzedInitializers));
                            includeInitializersInBody = false;
                            analyzedInitializers = null;
                        }
                        else
                        {
                            // These analyses check for diagnostics in lambdas.
                            // Control flow analysis and implicit return insertion are unnecessary.
                            DataFlowPass.Analyze(compilation, methodSymbol, analyzedInitializers, diagsForCurrentMethod, requireOutParamsAssigned: false);
                            DiagnosticsPass.IssueDiagnostics(compilation, analyzedInitializers, diagsForCurrentMethod, methodSymbol);
                        }
                    }
                }


#if DEBUG
                // If the method is a synthesized static or instance constructor, then debugImports will be null and we will use the value
                // from the first field initializer.
                if (this.generateDebugInfo)
                {
                    if ((methodSymbol.MethodKind == MethodKind.Constructor || methodSymbol.MethodKind == MethodKind.StaticConstructor) && 
                        methodSymbol.IsImplicitlyDeclared && body == null)
                    {
                        // There was no body to bind, so we didn't get anything from BindMethodBody.
                        Debug.Assert(debugImports == null);
                    }

                    // Either there were no field initializers or we grabbed debug imports from the first one.
                    Debug.Assert(processedInitializers.BoundInitializers.IsDefaultOrEmpty || processedInitializers.FirstDebugImports != null);
                }
#endif

                debugImports = debugImports ?? processedInitializers.FirstDebugImports;

                // Associate these debug imports with all methods generated from this one.
                compilationState.CurrentDebugImports = debugImports;

                if (body != null)
                {
                    DiagnosticsPass.IssueDiagnostics(compilation, body, diagsForCurrentMethod, methodSymbol);
                }

                BoundBlock flowAnalyzedBody = null;
                if (body != null)
                {
                    flowAnalyzedBody = FlowAnalysisPass.Rewrite(methodSymbol, body, diagsForCurrentMethod);
                }

                bool hasErrors = hasDeclarationErrors || diagsForCurrentMethod.HasAnyErrors() || processedInitializers.HasErrors;

                // Record whether or not the bound tree for the lowered method body (including any initializers) contained any
                // errors (note: errors, not diagnostics).
                SetGlobalErrorIfTrue(hasErrors);

                bool diagsWritten = false;
                var actualDiagnostics = diagsForCurrentMethod.ToReadOnly();
                if (sourceMethod != null)
                {
                    actualDiagnostics = sourceMethod.SetDiagnostics(actualDiagnostics, out diagsWritten);
                }

                if (diagsWritten && !methodSymbol.IsImplicitlyDeclared && compilation.EventQueue != null)
                {
                    var lazySemanticModel = body == null ? null : new Lazy<SemanticModel>(() =>
                    {
                        var syntax = body.Syntax;
                        var semanticModel = (CSharpSemanticModel)compilation.GetSemanticModel(syntax.SyntaxTree);
                        var memberModel = semanticModel.GetMemberModel(syntax);
                        if (memberModel != null)
                        {
                            memberModel.UnguardedAddBoundTreeForStandaloneSyntax(syntax, body);
                        }
                        return semanticModel;
                    });

                    compilation.EventQueue.Enqueue(new SymbolDeclaredCompilationEvent(compilation, methodSymbol, lazySemanticModel));
                }

                // Don't lower if we're not emitting or if there were errors. 
                // Methods that had binding errors are considered too broken to be lowered reliably.
                if (moduleBeingBuiltOpt == null || hasErrors)
                {
                    this.diagnostics.AddRange(actualDiagnostics);
                    return;
                }

                // ############################
                // LOWERING AND EMIT
                // Any errors generated below here are considered Emit diagnostics 
                // and will not be reported to callers Compilation.GetDiagnostics()

                bool hasBody = flowAnalyzedBody != null;
                VariableSlotAllocator variableSlotAllocatorOpt = null;
                StateMachineTypeSymbol stateMachineTypeOpt = null;
                BoundStatement loweredBodyOpt = null;

                if (hasBody)
                {
                    loweredBodyOpt = LowerBodyOrInitializer(
                        methodSymbol,
                        flowAnalyzedBody,
                        previousSubmissionFields,
                        compilationState,
                        diagsForCurrentMethod,
                        out stateMachineTypeOpt,
                        out variableSlotAllocatorOpt);

                    Debug.Assert(loweredBodyOpt != null);
                }
                else
                {
                    loweredBodyOpt = null;
                }

                hasErrors = hasErrors || (hasBody && loweredBodyOpt.HasErrors) || diagsForCurrentMethod.HasAnyErrors();
                SetGlobalErrorIfTrue(hasErrors);

                // don't emit if the resulting method would contain initializers with errors
                if (!hasErrors && (hasBody || includeInitializersInBody))
                {
                    // Fields must be initialized before constructor initializer (which is the first statement of the analyzed body, if specified),
                    // so that the initialization occurs before any method overridden by the declaring class can be invoked from the base constructor
                    // and access the fields.

                    ImmutableArray<BoundStatement> boundStatements;

                    if (methodSymbol.IsScriptConstructor)
                    {
                        boundStatements = MethodBodySynthesizer.ConstructScriptConstructorBody(loweredBodyOpt, methodSymbol, previousSubmissionFields, compilation);
                    }
                    else
                    {
                        boundStatements = ImmutableArray<BoundStatement>.Empty;

                        if (analyzedInitializers != null)
                        {
                            processedInitializers.LoweredInitializers = (BoundStatementList)LowerBodyOrInitializer(
                                methodSymbol,
                                analyzedInitializers,
                                previousSubmissionFields,
                                compilationState,
                                diagsForCurrentMethod,
                                out stateMachineTypeOpt,
                                out variableSlotAllocatorOpt);

                            // initializers can't produce state machines
                            Debug.Assert(stateMachineTypeOpt == null);

                            Debug.Assert(processedInitializers.LoweredInitializers.Kind == BoundKind.StatementList);
                            Debug.Assert(!hasErrors);
                            hasErrors = processedInitializers.LoweredInitializers.HasAnyErrors || diagsForCurrentMethod.HasAnyErrors();
                            SetGlobalErrorIfTrue(hasErrors);

                            if (hasErrors)
                            {
                                this.diagnostics.AddRange(diagsForCurrentMethod);
                                return;
                            }
                        }

                        // initializers for global code have already been included in the body
                        if (includeInitializersInBody)
                        {
                            boundStatements = boundStatements.Concat(processedInitializers.LoweredInitializers.Statements);
                        }

                        if (hasBody)
                        {
                            boundStatements = boundStatements.Concat(ImmutableArray.Create(loweredBodyOpt));
                        }
                    }

                    // generated struct constructors should ensure that all fields are assigned (even those that do not have initializers)
                    var container = methodSymbol.ContainingType as SourceMemberContainerTypeSymbol;
                    if (container != null &&
                        container.IsStructType() &&
                        methodSymbol.IsImplicitInstanceConstructor)
                    {
                        var chain = ChainImplicitStructConstructor(methodSymbol, container);
                        chain = LowerBodyOrInitializer(
                                methodSymbol,
                                chain,
                                previousSubmissionFields,
                                compilationState,
                                diagsForCurrentMethod,
                                out stateMachineTypeOpt,
                                out variableSlotAllocatorOpt);

                        boundStatements = boundStatements.Insert(0, chain);
                    }

                    CSharpSyntaxNode syntax = methodSymbol.GetNonNullSyntaxNode();

                    var boundBody = BoundStatementList.Synthesized(syntax, boundStatements);

                    var emittedBody = GenerateMethodBody(
                        moduleBeingBuiltOpt,
                        methodSymbol,
                        boundBody,
                        stateMachineTypeOpt,
                        variableSlotAllocatorOpt,
                        diagsForCurrentMethod,
                        debugDocumentProvider,
                        GetNamespaceScopes(methodSymbol, debugImports));

                    moduleBeingBuiltOpt.SetMethodBody(methodSymbol.PartialDefinitionPart ?? methodSymbol, emittedBody);
                }

                this.diagnostics.AddRange(diagsForCurrentMethod);
            }
            finally
            {
                diagsForCurrentMethod.Free();
                compilationState.CurrentDebugImports = oldDebugImports;
            }
        }

        /// <summary>
        /// Synthesised parameterlesss constructors in structs chain to the "default" constructor
        /// </summary>
        private BoundStatement ChainImplicitStructConstructor(MethodSymbol methodSymbol, SourceMemberContainerTypeSymbol containingType)
        {
            CSharpSyntaxNode syntax = methodSymbol.GetNonNullSyntaxNode();

            // TODO: can we skip this if we have as many initializers as instance fields?
            //       there could be an observable difference if initializer crashes 
            //       and constructor is invoked in-place and the partially initialized 
            //       instance escapes. (impossible in C#, I beleive)
            //
            // add "this = default(T)" at the beginning of implicit struct ctor
            return new BoundExpressionStatement(syntax,
                    new BoundAssignmentOperator(
                    syntax,
                    new BoundThisReference(syntax, containingType),
                    new BoundDefaultOperator(syntax, containingType),
                    RefKind.None,
                    containingType));
        }

        // internal for testing
        internal static BoundStatement LowerBodyOrInitializer(
            MethodSymbol method,
            BoundStatement body,
            SynthesizedSubmissionFields previousSubmissionFields,
            TypeCompilationState compilationState,
            DiagnosticBag diagnostics,
            out StateMachineTypeSymbol stateMachineTypeOpt,
            out VariableSlotAllocator variableSlotAllocatorOpt)
        {
            Debug.Assert(compilationState.ModuleBuilderOpt != null);
            stateMachineTypeOpt = null;
            variableSlotAllocatorOpt = null;

            if (body.HasErrors)
            {
                return body;
            }

            bool sawLambdas;
            bool sawDynamicOperations;
            bool sawAwaitInExceptionHandler;
            var loweredBody = LocalRewriter.Rewrite(
                method.DeclaringCompilation,
                method,
                method.ContainingType,
                body,
                compilationState,
                previousSubmissionFields: previousSubmissionFields,
                allowOmissionOfConditionalCalls: true,
                diagnostics: diagnostics,
                sawLambdas: out sawLambdas,
                sawDynamicOperations: out sawDynamicOperations,
                sawAwaitInExceptionHandler: out sawAwaitInExceptionHandler);

            if (sawDynamicOperations && compilationState.ModuleBuilderOpt.IsEncDelta)
            {
                // Dynamic operations are not supported in ENC.
                var location = method.Locations[0];
                diagnostics.Add(new CSDiagnosticInfo(ErrorCode.ERR_EncNoDynamicOperation), location);
                return loweredBody;
            }

            if (loweredBody.HasErrors)
            {
                return loweredBody;
            }

            if (sawAwaitInExceptionHandler)
            {
                // If we have awaits in handlers, we need to 
                // replace handlers with synthetic ones which can be consumed by async rewriter.
                // The reason why this rewrite happens before the lambda rewrite 
                // is that we may need access to exception locals and it would be fairly hard to do
                // if these locals are captured into closures (possibly nested ones).
                Debug.Assert(!method.IsIterator);
                loweredBody = AsyncExceptionHandlerRewriter.Rewrite(
                    method,
                    method.ContainingType,
                    loweredBody,
                    compilationState,
                    diagnostics);
            }

            if (loweredBody.HasErrors)
            {
                return loweredBody;
            }

            BoundStatement bodyWithoutLambdas = loweredBody;
            if (sawLambdas)
            {
                var lambdaAnalysis = LambdaRewriter.Analysis.Analyze(loweredBody, method);
                if (lambdaAnalysis.SeenLambda)
                {
                    bodyWithoutLambdas = LambdaRewriter.Rewrite(loweredBody, method.ContainingType, method.ThisParameter, method, compilationState, diagnostics, lambdaAnalysis);
                }
            }

            if (bodyWithoutLambdas.HasErrors)
            {
                return bodyWithoutLambdas;
            }

            variableSlotAllocatorOpt = compilationState.ModuleBuilderOpt.TryCreateVariableSlotAllocator(method);

            IteratorStateMachine iteratorStateMachine;
            BoundStatement bodyWithoutIterators = IteratorRewriter.Rewrite(bodyWithoutLambdas, method, variableSlotAllocatorOpt, compilationState, diagnostics, out iteratorStateMachine);

            if (bodyWithoutIterators.HasErrors)
            {
                return bodyWithoutIterators;
            }

            AsyncStateMachine asyncStateMachine;
            BoundStatement bodyWithoutAsync = AsyncRewriter.Rewrite(bodyWithoutIterators, method, variableSlotAllocatorOpt, compilationState, diagnostics, out asyncStateMachine);

            Debug.Assert(iteratorStateMachine == null || asyncStateMachine == null);
            stateMachineTypeOpt = (StateMachineTypeSymbol)iteratorStateMachine ?? asyncStateMachine;

            return bodyWithoutAsync;
        }

        private static MethodBody GenerateMethodBody(
            PEModuleBuilder moduleBuilder,
            MethodSymbol method,
            BoundStatement block, 
            StateMachineTypeSymbol stateMachineTypeOpt,
            VariableSlotAllocator variableSlotAllocatorOpt,
            DiagnosticBag diagnostics,
            DebugDocumentProvider debugDocumentProvider,
            ImmutableArray<Cci.NamespaceScope> namespaceScopes)
        {
            // Note: don't call diagnostics.HasAnyErrors() in release; could be expensive if compilation has many warnings.
            Debug.Assert(!diagnostics.HasAnyErrors(), "Running code generator when errors exist might be dangerous; code generator not expecting errors");

            bool emittingPdbs = !namespaceScopes.IsDefault;
            var compilation = moduleBuilder.Compilation;
            var localSlotManager = new LocalSlotManager(variableSlotAllocatorOpt);
            var optimizations = compilation.Options.OptimizationLevel;

            ILBuilder builder = new ILBuilder(moduleBuilder, localSlotManager, optimizations);
            DiagnosticBag diagnosticsForThisMethod = DiagnosticBag.GetInstance();
            try
            {
                Cci.AsyncMethodBodyDebugInfo asyncDebugInfo = null;

                var codeGen = new CodeGen.CodeGenerator(method, block, builder, moduleBuilder, diagnosticsForThisMethod, optimizations, emittingPdbs);

                // We need to save additional debugging information for MoveNext of an async state machine.
                var stateMachineMethod = method as SynthesizedStateMachineMethod;
                bool isStateMachineMoveNextMethod = stateMachineMethod != null && method.Name == WellKnownMemberNames.MoveNextMethodName;

                if (isStateMachineMoveNextMethod && stateMachineMethod.StateMachineType.KickoffMethod.IsAsync)
                {
                    int asyncCatchHandlerOffset;
                    ImmutableArray<int> asyncYieldPoints;
                    ImmutableArray<int> asyncResumePoints;
                    codeGen.Generate(out asyncCatchHandlerOffset, out asyncYieldPoints, out asyncResumePoints);

                    asyncDebugInfo = new Cci.AsyncMethodBodyDebugInfo(stateMachineMethod.StateMachineType.KickoffMethod, asyncCatchHandlerOffset, asyncYieldPoints, asyncResumePoints);
                }
                else
                {
                    codeGen.Generate();
                }

                var localVariables = builder.LocalSlotManager.LocalsInOrder();

                if (localVariables.Length > 0xFFFE)
                {
                    diagnosticsForThisMethod.Add(ErrorCode.ERR_TooManyLocals, method.Locations.First());
                }

                if (diagnosticsForThisMethod.HasAnyErrors())
                {
                    // we are done here. Since there were errors we should not emit anything.
                    return null;
                }

                // We will only save the IL builders when running tests.
                if (moduleBuilder.SaveTestData)
                {
                    moduleBuilder.SetMethodTestData(method, builder.GetSnapshot());
                }

                // Only compiler-generated MoveNext methods have iterator scopes.  See if this is one.
                var stateMachineHoistedLocalScopes = default(ImmutableArray<Cci.StateMachineHoistedLocalScope>);
                if (isStateMachineMoveNextMethod)
                {
                    // In C#, hoisted local scopes are edge-inclusive.
                    stateMachineHoistedLocalScopes = builder.GetHoistedLocalScopes(edgeInclusive: true);
                }

                var stateMachineHoistedLocalSlots = default(ImmutableArray<EncHoistedLocalInfo>);
                var stateMachineAwaiterSlots = default(ImmutableArray<Cci.ITypeReference>);
                if (optimizations == OptimizationLevel.Debug && stateMachineTypeOpt != null)
                {
                    Debug.Assert(method.IsAsync || method.IsIterator);
                    GetStateMachineSlotDebugInfo(moduleBuilder.GetSynthesizedFields(stateMachineTypeOpt), out stateMachineHoistedLocalSlots, out stateMachineAwaiterSlots);
                }

                return new MethodBody(
                    builder.RealizedIL,
                    builder.MaxStack,
                    method.PartialDefinitionPart ?? method,
                    localVariables,
                    builder.RealizedSequencePoints,
                    debugDocumentProvider,
                    builder.RealizedExceptionHandlers,
                    builder.GetAllScopes(),
                    builder.HasDynamicLocal,
                    namespaceScopes,
                    Cci.NamespaceScopeEncoding.InPlace,
                    stateMachineTypeOpt?.Name,
                    stateMachineHoistedLocalScopes,
                    stateMachineHoistedLocalSlots,
                    stateMachineAwaiterSlots,
                    asyncDebugInfo);
            }
            finally
            {
                // Basic blocks contain poolable builders for IL and sequence points. Free those back
                // to their pools.
                builder.FreeBasicBlocks();

                // Remember diagnostics.
                diagnostics.AddRange(diagnosticsForThisMethod);
                diagnosticsForThisMethod.Free();
            }
        }

        private static void GetStateMachineSlotDebugInfo(
            IEnumerable<Cci.IFieldDefinition> fieldDefs, 
            out ImmutableArray<EncHoistedLocalInfo> hoistedVariableSlots,
            out ImmutableArray<Cci.ITypeReference> awaiterSlots)
        {
            var hoistedVariables = ArrayBuilder<EncHoistedLocalInfo>.GetInstance();
            var awaiters = ArrayBuilder<Cci.ITypeReference>.GetInstance();

            foreach (StateMachineFieldSymbol field in fieldDefs)
            {
                int index = field.SlotIndex;

                if (field.SlotDebugInfo.SynthesizedKind == SynthesizedLocalKind.AwaiterField)
                {
                    Debug.Assert(index >= 0);

                    while (index >= awaiters.Count)
                    {
                        awaiters.Add(null);
                    }

                    awaiters[index] = (Cci.ITypeReference)field.Type;
                }
                else if (!field.SlotDebugInfo.Id.IsNone)
                {
                    Debug.Assert(index >= 0 && field.SlotDebugInfo.SynthesizedKind.IsLongLived());

                    while (index >= hoistedVariables.Count)
                    {
                        // Empty slots may be present if variables were deleted during EnC.
                        hoistedVariables.Add(new EncHoistedLocalInfo());
                    }

                    hoistedVariables[index] = new EncHoistedLocalInfo(field.SlotDebugInfo, (Cci.ITypeReference)field.Type);
                }
            }

            hoistedVariableSlots = hoistedVariables.ToImmutableAndFree();
            awaiterSlots = awaiters.ToImmutableAndFree();
        }

        // NOTE: can return null if the method has no body.
        internal static BoundBlock BindMethodBody(MethodSymbol method, TypeCompilationState compilationState, DiagnosticBag diagnostics)
        {
            ConsList<Imports> unused;
            return BindMethodBody(method, compilationState, diagnostics, false, out unused);
        }

        // NOTE: can return null if the method has no body.
        private static BoundBlock BindMethodBody(MethodSymbol method, TypeCompilationState compilationState, DiagnosticBag diagnostics, bool generateDebugInfo, out ConsList<Imports> debugImports)
        {
            debugImports = null;

            var compilation = method.DeclaringCompilation;
            BoundStatement constructorInitializer = null;

            // delegates have constructors but not constructor initializers
            if (method.MethodKind == MethodKind.Constructor && !method.ContainingType.IsDelegateType() && !method.IsExtern)
            {
                var initializerInvocation = BindConstructorInitializer(method, diagnostics, compilation);

                if (initializerInvocation != null)
                {
                    constructorInitializer = new BoundExpressionStatement(initializerInvocation.Syntax, initializerInvocation) { WasCompilerGenerated = true };
                    Debug.Assert(initializerInvocation.HasAnyErrors || constructorInitializer.IsConstructorInitializer(), "Please keep this bound node in sync with BoundNodeExtensions.IsConstructorInitializer.");
                }
            }

            BoundBlock body;

            var sourceMethod = method as SourceMethodSymbol;
            if ((object)sourceMethod != null)
            {
                if (sourceMethod.IsExtern)
                {
                    if (sourceMethod.BodySyntax == null && (sourceMethod.SyntaxNode as ConstructorDeclarationSyntax)?.Initializer == null)
                    {
                        // Generate warnings only if we are not generating ERR_ExternHasBody or ERR_ExternHasConstructorInitializer errors
                        GenerateExternalMethodWarnings(sourceMethod, diagnostics);
                    }

                    return null;
                }

                if (sourceMethod.IsDefaultValueTypeConstructor())
                {
                    // No body for default struct constructor.
                    return null;
                }

                var factory = compilation.GetBinderFactory(sourceMethod.SyntaxTree);

                var blockSyntax = sourceMethod.BodySyntax as BlockSyntax;

                if (blockSyntax != null)
                {
                    var inMethodBinder = factory.GetBinder(blockSyntax);

                    Binder binder = new ExecutableCodeBinder(blockSyntax, sourceMethod, inMethodBinder);
                    body = binder.BindBlock(blockSyntax, diagnostics);
                    if (generateDebugInfo)
                    {
                        debugImports = binder.ImportsList;
                    }

                    if (inMethodBinder.IsDirectlyInIterator)
                    {
                        foreach (var parameter in method.Parameters)
                        {
                            if (parameter.RefKind != RefKind.None)
                            {
                                diagnostics.Add(ErrorCode.ERR_BadIteratorArgType, parameter.Locations[0]);
                            }
                            else if (parameter.Type.IsUnsafe())
                            {
                                diagnostics.Add(ErrorCode.ERR_UnsafeIteratorArgType, parameter.Locations[0]);
                            }
                        }

                        if (sourceMethod.IsUnsafe && compilation.Options.AllowUnsafe) // Don't cascade
                        {
                            diagnostics.Add(ErrorCode.ERR_IllegalInnerUnsafe, sourceMethod.Locations[0]);
                        }

                        if (sourceMethod.IsVararg)
                        {
                            // error CS1636: __arglist is not allowed in the parameter list of iterators
                            diagnostics.Add(ErrorCode.ERR_VarargsIterator, sourceMethod.Locations[0]);
                        }
                    }
                }
                else if (sourceMethod.IsExpressionBodied)
                {
                    var methodSyntax = sourceMethod.SyntaxNode;
                    var arrowExpression = methodSyntax.GetExpressionBodySyntax();

                    Binder binder = factory.GetBinder(arrowExpression);
                    binder = new ExecutableCodeBinder(arrowExpression, sourceMethod, binder);
                    // Add locals
                    return binder.BindExpressionBodyAsBlock(arrowExpression, diagnostics);
                }
                else
                {
                    var property = sourceMethod.AssociatedSymbol as SourcePropertySymbol;
                    if ((object)property != null && property.IsAutoProperty)
                    {
                        return MethodBodySynthesizer.ConstructAutoPropertyAccessorBody(sourceMethod);
                    }

                    return null;
                }
            }
            else
            {
                //  synthesized methods should return their bound bodies 
                body = null;
            }

            var statements = ArrayBuilder<BoundStatement>.GetInstance();

            if (constructorInitializer != null)
            {
                statements.Add(constructorInitializer);
            }

            if (body != null)
            {
                statements.Add(body);
            }

            CSharpSyntaxNode syntax = body != null ? body.Syntax : method.GetNonNullSyntaxNode();
            BoundBlock block;

            if (statements.Count == 1 && statements[0].Kind == ((body == null) ? BoundKind.Block : body.Kind))
            {
                // most common case - we just have a single block for the body.
                block = (BoundBlock)statements[0];
                statements.Free();
            }
            else
            {
                block = new BoundBlock(syntax, ImmutableArray<LocalSymbol>.Empty, statements.ToImmutableAndFree()) { WasCompilerGenerated = true };
            }

            return method.MethodKind == MethodKind.Destructor ? MethodBodySynthesizer.ConstructDestructorBody(syntax, method, block) : block;
        }

        /// <summary>
        /// Bind the (implicit or explicit) constructor initializer of a constructor symbol.
        /// </summary>
        /// <param name="constructor">Constructor method.</param>
        /// <param name="diagnostics">Accumulates errors (e.g. access "this" in constructor initializer).</param>
        /// <param name="compilation">Used to retrieve binder.</param>
        /// <returns>A bound expression for the constructor initializer call.</returns>
        internal static BoundExpression BindConstructorInitializer(MethodSymbol constructor, DiagnosticBag diagnostics, CSharpCompilation compilation)
        {
            // Note that the base type can be null if we're compiling System.Object in source.
            NamedTypeSymbol baseType = constructor.ContainingType.BaseTypeNoUseSiteDiagnostics;

            SourceMethodSymbol sourceConstructor = constructor as SourceMethodSymbol;
            ConstructorDeclarationSyntax constructorSyntax = null;
            ArgumentListSyntax initializerArgumentListOpt = null;
            if ((object)sourceConstructor != null)
            {
                constructorSyntax = (ConstructorDeclarationSyntax)sourceConstructor.SyntaxNode;
                if (constructorSyntax.Initializer != null)
                {
                    initializerArgumentListOpt = constructorSyntax.Initializer.ArgumentList;
                }
            }

            // The common case is that we have no constructor initializer and the type inherits directly from object.
            // Also, we might be trying to generate a constructor for an entirely compiler-generated class such
            // as a closure class; in that case it is vexing to try to find a suitable binder for the non-existing
            // constructor syntax so that we can do unnecessary overload resolution on the non-existing initializer!
            // Simply take the early out: bind directly to the parameterless object ctor rather than attempting
            // overload resolution.
            if (initializerArgumentListOpt == null && (object)baseType != null)
            {
                if (baseType.SpecialType == SpecialType.System_Object)
                {
                    return GenerateObjectConstructorInitializer(constructor, diagnostics);
                }
                else if (baseType.IsErrorType() || baseType.IsStatic)
                {
                    // If the base type is bad and there is no initializer then we can just bail.
                    // We have no expressions we need to analyze to report errors on.
                    return null;
                }
            }

            // Either our base type is not object, or we have an initializer syntax, or both. We're going to
            // need to do overload resolution on the set of constructors of the base type, either on
            // the provided initializer syntax, or on an implicit ": base()" syntax.

            // SPEC ERROR: The specification states that if you have the situation 
            // SPEC ERROR: class B { ... } class D1 : B {} then the default constructor
            // SPEC ERROR: generated for D1 must call an accessible *parameterless* constructor
            // SPEC ERROR: in B. However, it also states that if you have 
            // SPEC ERROR: class B { ... } class D2 : B { D2() {} }  or
            // SPEC ERROR: class B { ... } class D3 : B { D3() : base() {} }  then
            // SPEC ERROR: the compiler performs *overload resolution* to determine
            // SPEC ERROR: which accessible constructor of B is called. Since B might have
            // SPEC ERROR: a ctor with all optional parameters, overload resolution might
            // SPEC ERROR: succeed even if there is no parameterless constructor. This
            // SPEC ERROR: is unintentionally inconsistent, and the native compiler does not
            // SPEC ERROR: implement this behavior. Rather, we should say in the spec that
            // SPEC ERROR: if there is no ctor in D1, then a ctor is created for you exactly
            // SPEC ERROR: as though you'd said "D1() : base() {}". 
            // SPEC ERROR: This is what we now do in Roslyn.

            // Now, in order to do overload resolution, we're going to need a binder. There are
            // three possible situations:
            //
            // class D1 : B { }
            // class D2 : B { D2(int x) { } }
            // class D3 : B { D3(int x) : base(x) { } }
            //
            // In the first case the binder needs to be the binder associated with
            // the *body* of D1 because if the base class ctor is protected, we need
            // to be inside the body of a derived class in order for it to be in the
            // accessibility domain of the protected base class ctor.
            //
            // In the second case the binder could be the binder associated with 
            // the body of D2; since the implicit call to base() will have no arguments
            // there is no need to look up "x".
            // 
            // In the third case the binder must be the binder that knows about "x" 
            // because x is in scope.

            Binder outerBinder;

            if ((object)sourceConstructor == null)
            {
                // The constructor is implicit. We need to get the binder for the body
                // of the enclosing class. 
                CSharpSyntaxNode containerNode = constructor.GetNonNullSyntaxNode();
                SyntaxToken bodyToken;
                if (containerNode.Kind() == SyntaxKind.ClassDeclaration)
                {
                    bodyToken = ((ClassDeclarationSyntax)containerNode).OpenBraceToken;
                }
                else if (containerNode.Kind() == SyntaxKind.StructDeclaration)
                {
                    bodyToken = ((StructDeclarationSyntax)containerNode).OpenBraceToken;
                }
                else if (containerNode.Kind() == SyntaxKind.EnumDeclaration)
                {
                    // We're not going to find any non-default ctors, but we'll look anyway.
                    bodyToken = ((EnumDeclarationSyntax)containerNode).OpenBraceToken;
                }
                else
                {
                    Debug.Assert(false, "How did we get an implicit constructor added to something that is neither a class nor a struct?");
                    bodyToken = containerNode.GetFirstToken();
                }

                outerBinder = compilation.GetBinderFactory(containerNode.SyntaxTree).GetBinder(containerNode, bodyToken.Position);
            }
            else if (initializerArgumentListOpt == null)
            {
                // We have a ctor in source but no explicit constructor initializer.  We can't just use the binder for the
                // type containing the ctor because the ctor might be marked unsafe.  Use the binder for the parameter list
                // as an approximation - the extra symbols won't matter because there are no identifiers to bind.

                outerBinder = compilation.GetBinderFactory(sourceConstructor.SyntaxTree).GetBinder(constructorSyntax.ParameterList);
            }
            else
            {
                outerBinder = compilation.GetBinderFactory(sourceConstructor.SyntaxTree).GetBinder(initializerArgumentListOpt);
            }

            //wrap in ConstructorInitializerBinder for appropriate errors
            Binder initializerBinder = outerBinder.WithAdditionalFlagsAndContainingMemberOrLambda(BinderFlags.ConstructorInitializer, constructor);

            return initializerBinder.BindConstructorInitializer(initializerArgumentListOpt, constructor, diagnostics);
        }

        internal static BoundCall GenerateObjectConstructorInitializer(MethodSymbol constructor, DiagnosticBag diagnostics)
        {
            NamedTypeSymbol objectType = constructor.ContainingType.BaseTypeNoUseSiteDiagnostics;
            Debug.Assert(objectType.SpecialType == SpecialType.System_Object);
            MethodSymbol objectConstructor = null;
            LookupResultKind resultKind = LookupResultKind.Viable;

            foreach (MethodSymbol objectCtor in objectType.InstanceConstructors)
            {
                if (objectCtor.ParameterCount == 0)
                {
                    objectConstructor = objectCtor;
                    break;
                }
            }

            // UNDONE: If this happens then something is deeply wrong. Should we give a better error?
            if ((object)objectConstructor == null)
            {
                diagnostics.Add(ErrorCode.ERR_BadCtorArgCount, constructor.Locations[0], objectType, /*desired param count*/ 0);
                return null;
            }

            // UNDONE: If this happens then something is deeply wrong. Should we give a better error?
            bool hasErrors = false;
            HashSet<DiagnosticInfo> useSiteDiagnostics = null;
            if (!AccessCheck.IsSymbolAccessible(objectConstructor, constructor.ContainingType, ref useSiteDiagnostics))
            {
                diagnostics.Add(ErrorCode.ERR_BadAccess, constructor.Locations[0], objectConstructor);
                resultKind = LookupResultKind.Inaccessible;
                hasErrors = true;
            }

            if (!useSiteDiagnostics.IsNullOrEmpty())
            {
                diagnostics.Add(constructor.Locations.IsEmpty ? NoLocation.Singleton : constructor.Locations[0], useSiteDiagnostics);
            }

            CSharpSyntaxNode syntax = constructor.GetNonNullSyntaxNode();

            BoundExpression receiver = new BoundThisReference(syntax, constructor.ContainingType) { WasCompilerGenerated = true };
            return new BoundCall(
                syntax: syntax,
                receiverOpt: receiver,
                method: objectConstructor,
                arguments: ImmutableArray<BoundExpression>.Empty,
                argumentNamesOpt: ImmutableArray<string>.Empty,
                argumentRefKindsOpt: ImmutableArray<RefKind>.Empty,
                isDelegateCall: false,
                expanded: false,
                invokedAsExtensionMethod: false,
                argsToParamsOpt: ImmutableArray<int>.Empty,
                resultKind: resultKind,
                type: objectType,
                hasErrors: hasErrors)
            { WasCompilerGenerated = true };
        }

        private static void GenerateExternalMethodWarnings(SourceMethodSymbol methodSymbol, DiagnosticBag diagnostics)
        {
            if (methodSymbol.GetAttributes().IsEmpty && !methodSymbol.ContainingType.IsComImport)
            {
                // external method with no attributes
                var errorCode = (methodSymbol.MethodKind == MethodKind.Constructor || methodSymbol.MethodKind == MethodKind.StaticConstructor) ?
                    ErrorCode.WRN_ExternCtorNoImplementation :
                    ErrorCode.WRN_ExternMethodNoImplementation;
                diagnostics.Add(errorCode, methodSymbol.Locations[0], methodSymbol);
            }
        }

        /// <summary>
        /// Returns true if the method is a constructor and has a this() constructor initializer.
        /// </summary>
        private static bool HasThisConstructorInitializer(MethodSymbol method)
        {
            if ((object)method != null && method.MethodKind == MethodKind.Constructor)
            {
                SourceMethodSymbol sourceMethod = method as SourceMethodSymbol;
                if ((object)sourceMethod != null)
                {
                    ConstructorDeclarationSyntax constructorSyntax = sourceMethod.SyntaxNode as ConstructorDeclarationSyntax;
                    if (constructorSyntax != null)
                    {
                        ConstructorInitializerSyntax initializerSyntax = constructorSyntax.Initializer;
                        if (initializerSyntax != null)
                        {
                            return initializerSyntax.Kind() == SyntaxKind.ThisConstructorInitializer;
                        }
                    }
                }
            }

            return false;
        }

        private static Cci.DebugSourceDocument CreateDebugDocumentForFile(string normalizedPath)
        {
            return new Cci.DebugSourceDocument(normalizedPath, Cci.DebugSourceDocument.CorSymLanguageTypeCSharp);
        }

        private static bool PassesFilter(Predicate<Symbol> filterOpt, Symbol symbol)
        {
            return (filterOpt == null) || filterOpt(symbol);
        }
    }
}
