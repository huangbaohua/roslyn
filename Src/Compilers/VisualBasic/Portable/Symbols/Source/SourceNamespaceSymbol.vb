' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis.ImmutableArrayExtensions
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.Symbols
    Friend Class SourceNamespaceSymbol
        Inherits PEOrSourceOrMergedNamespaceSymbol

        Private ReadOnly m_declaration As MergedNamespaceDeclaration
        Private ReadOnly m_containingNamespace As SourceNamespaceSymbol
        Private ReadOnly m_containingModule As SourceModuleSymbol
        Private m_nameToMembersMap As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))
        Private m_nameToTypeMembersMap As Dictionary(Of String, ImmutableArray(Of NamedTypeSymbol))
        Private m_lazyEmbeddedKind As Integer = EmbeddedSymbolKind.Unset

        ' lazily evaluated state of the symbol (StateFlags)
        Private m_lazyState As Integer

        <Flags>
        Private Enum StateFlags As Integer
            HasMultipleSpellings = &H1     ' ReadOnly: Set if there are multiple declarations with different spellings (casing)
            AllMembersIsSorted = &H2       ' Set if "m_lazyAllMembers" is sorted.
            DeclarationValidated = &H4     ' Set by ValidateDeclaration.
        End Enum

        ' This caches results of GetModuleMembers()
        Private m_lazyModuleMembers As ImmutableArray(Of NamedTypeSymbol)

        ' This caches results of GetMembers()
        Private m_lazyAllMembers As ImmutableArray(Of Symbol)

        Private m_lazyLexicalSortKey As LexicalSortKey = LexicalSortKey.NotInitialized

        Friend Sub New(decl As MergedNamespaceDeclaration, containingNamespace As SourceNamespaceSymbol, containingModule As SourceModuleSymbol)
            m_declaration = decl
            m_containingNamespace = containingNamespace
            m_containingModule = containingModule
            If (containingNamespace IsNot Nothing AndAlso containingNamespace.HasMultipleSpellings) OrElse decl.HasMultipleSpellings Then
                m_lazyState = StateFlags.HasMultipleSpellings
            End If
        End Sub

        ''' <summary>
        ''' Register COR types declared in this namespace, if any, in the COR types cache.
        ''' </summary>
        Private Sub RegisterDeclaredCorTypes()

            Dim containingAssembly As AssemblySymbol = Me.ContainingAssembly

            If (containingAssembly.KeepLookingForDeclaredSpecialTypes) Then
                ' Register newly declared COR types
                For Each array In m_nameToMembersMap.Values
                    For Each member In array
                        Dim type = TryCast(member, NamedTypeSymbol)
                        If type IsNot Nothing AndAlso type.SpecialType <> SpecialType.None Then
                            containingAssembly.RegisterDeclaredSpecialType(type)

                            If Not containingAssembly.KeepLookingForDeclaredSpecialTypes Then
                                Return
                            End If
                        End If
                    Next
                Next
            End If
        End Sub

        Public Overrides ReadOnly Property Name As String
            Get
                Return m_declaration.Name
            End Get
        End Property

        Friend Overrides ReadOnly Property EmbeddedSymbolKind As EmbeddedSymbolKind
            Get
                If m_lazyEmbeddedKind = EmbeddedSymbolKind.Unset Then
                    Dim value As Integer = EmbeddedSymbolKind.None
                    For Each location In m_declaration.NameLocations
                        Debug.Assert(location IsNot Nothing)
                        If location.Kind = LocationKind.None Then
                            Dim embeddedLocation = TryCast(location, EmbeddedTreeLocation)
                            If embeddedLocation IsNot Nothing Then
                                value = value Or embeddedLocation.EmbeddedKind
                            End If
                        End If
                    Next
                    Interlocked.CompareExchange(m_lazyEmbeddedKind, value, EmbeddedSymbolKind.Unset)
                End If

                Return CType(m_lazyEmbeddedKind, EmbeddedSymbolKind)
            End Get
        End Property

        Public Overrides ReadOnly Property ContainingSymbol As Symbol
            Get
                Return If(m_containingNamespace, DirectCast(m_containingModule, Symbol))
            End Get
        End Property

        Public Overrides ReadOnly Property ContainingAssembly As AssemblySymbol
            Get
                Return m_containingModule.ContainingAssembly
            End Get
        End Property

        Public Overrides ReadOnly Property ContainingModule As ModuleSymbol
            Get
                Return Me.m_containingModule
            End Get
        End Property

        Friend Overrides ReadOnly Property Extent As NamespaceExtent
            Get
                Return New NamespaceExtent(m_containingModule)
            End Get
        End Property

        Private ReadOnly Property NameToMembersMap As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))
            Get
                Return GetNameToMembersMap()
            End Get
        End Property

        Private Function GetNameToMembersMap() As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))
            If m_nameToMembersMap Is Nothing Then
                Dim map = MakeNameToMembersMap()
                If Interlocked.CompareExchange(m_nameToMembersMap, map, Nothing) Is Nothing Then
                    RegisterDeclaredCorTypes()
                End If
            End If

            Return m_nameToMembersMap
        End Function

        Private Function MakeNameToMembersMap() As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))
            ' NOTE: Even though the resulting map stores ImmutableArray(Of NamespaceOrTypeSymbol) as 
            ' NOTE: values if the name is mapped into an array of named types, which is frequently 
            ' NOTE: the case, we actually create an array of NamedTypeSymbol[] and wrap it in 
            ' NOTE: ImmutableArray(Of NamespaceOrTypeSymbol) 
            ' NOTE: 
            ' NOTE: This way we can save time and memory in GetNameToTypeMembersMap() -- when we see that
            ' NOTE: a name maps into values collection containing types only instead of allocating another 
            ' NOTE: array of NamedTypeSymbol[] we downcast the array to ImmutableArray(Of NamedTypeSymbol)

            Dim builder As New NameToSymbolMapBuilder(m_declaration.Children.Length)
            For Each declaration In m_declaration.Children
                builder.Add(BuildSymbol(declaration))
            Next

            ' TODO(cyrusn): The C# and VB impls differ here.  C# reports errors here and VB does not.
            ' Is that what we want?

            Return builder.CreateMap()
        End Function

        Private Structure NameToSymbolMapBuilder
            Private ReadOnly dictionary As Dictionary(Of String, Object)

            Public Sub New(capacity As Integer)
                dictionary = New Dictionary(Of String, Object)(capacity, IdentifierComparison.Comparer)
            End Sub

            Public Sub Add(symbol As NamespaceOrTypeSymbol)
                Dim name As String = symbol.Name
                Dim item As Object = Nothing

                If Me.dictionary.TryGetValue(name, item) Then
                    Dim builder = TryCast(item, ArrayBuilder(Of NamespaceOrTypeSymbol))
                    If builder Is Nothing Then
                        builder = ArrayBuilder(Of NamespaceOrTypeSymbol).GetInstance()
                        builder.Add(DirectCast(item, NamespaceOrTypeSymbol))
                        Me.dictionary(name) = builder
                    End If
                    builder.Add(symbol)

                Else
                    Me.dictionary(name) = symbol
                End If

            End Sub

            Public Function CreateMap() As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))
                Dim result As New Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol))(Me.dictionary.Count, IdentifierComparison.Comparer)

                For Each kvp In Me.dictionary

                    Dim value As Object = kvp.Value
                    Dim members As ImmutableArray(Of NamespaceOrTypeSymbol)

                    Dim builder = TryCast(value, ArrayBuilder(Of NamespaceOrTypeSymbol))
                    If builder IsNot Nothing Then
                        Debug.Assert(builder.Count > 1)
                        Dim hasNamespaces As Boolean = False

                        For i = 0 To builder.Count - 1
                            If builder(i).Kind = SymbolKind.Namespace Then
                                hasNamespaces = True
                                Exit For
                            End If
                        Next

                        If hasNamespaces Then
                            members = builder.ToImmutable()
                        Else
                            members = StaticCast(Of NamespaceOrTypeSymbol).From(builder.ToDowncastedImmutable(Of NamedTypeSymbol)())
                        End If

                        builder.Free()
                    Else
                        Dim symbol = DirectCast(value, NamespaceOrTypeSymbol)
                        If symbol.Kind = SymbolKind.Namespace Then
                            members = ImmutableArray.Create(Of NamespaceOrTypeSymbol)(symbol)
                        Else
                            members = StaticCast(Of NamespaceOrTypeSymbol).From(ImmutableArray.Create(Of NamedTypeSymbol)(DirectCast(symbol, NamedTypeSymbol)))
                        End If
                    End If

                    result.Add(kvp.Key, members)
                Next

                Return result
            End Function
        End Structure

        Private Function BuildSymbol(decl As MergedNamespaceOrTypeDeclaration) As NamespaceOrTypeSymbol
            Dim namespaceDecl = TryCast(decl, MergedNamespaceDeclaration)
            If namespaceDecl IsNot Nothing Then
                Return New SourceNamespaceSymbol(namespaceDecl, Me, m_containingModule)
            Else
                Dim typeDecl = DirectCast(decl, MergedTypeDeclaration)
#If DEBUG Then
                ' Ensure that the type declaration is either from user code or embedded
                ' code, but not merged accross embedded code/user code boundary.
                Dim embedded = EmbeddedSymbolKind.Unset
                For Each ref In typeDecl.SyntaxReferences
                    Dim refKind = ref.SyntaxTree.GetEmbeddedKind()
                    If embedded <> EmbeddedSymbolKind.Unset Then
                        Debug.Assert(embedded = refKind)
                    Else
                        embedded = refKind
                    End If
                Next
                Debug.Assert(embedded <> EmbeddedSymbolKind.Unset)
#End If
                Return SourceNamedTypeSymbol.Create(typeDecl, Me, m_containingModule)
            End If
        End Function

        Private Function GetNameToTypeMembersMap() As Dictionary(Of String, ImmutableArray(Of NamedTypeSymbol))
            If m_nameToTypeMembersMap Is Nothing Then

                ' NOTE: This method depends on MakeNameToMembersMap() on creating a proper 
                ' NOTE: type of the array, see comments in MakeNameToMembersMap() for details

                Dim dictionary As New Dictionary(Of String, ImmutableArray(Of NamedTypeSymbol))(CaseInsensitiveComparison.Comparer)

                Dim map As Dictionary(Of String, ImmutableArray(Of NamespaceOrTypeSymbol)) = Me.GetNameToMembersMap()
                For Each kvp In map
                    Dim members As ImmutableArray(Of NamespaceOrTypeSymbol) = kvp.Value

                    Dim hasType As Boolean = False
                    Dim hasNamespace As Boolean = False

                    For Each symbol In members
                        If symbol.Kind = SymbolKind.NamedType Then
                            hasType = True
                            If hasNamespace Then
                                Exit For
                            End If

                        Else
                            Debug.Assert(symbol.Kind = SymbolKind.Namespace)
                            hasNamespace = True
                            If hasType Then
                                Exit For
                            End If
                        End If
                    Next

                    If hasType Then
                        If hasNamespace Then
                            dictionary.Add(kvp.Key, members.OfType(Of NamedTypeSymbol).AsImmutable())
                        Else
                            dictionary.Add(kvp.Key, members.As(Of NamedTypeSymbol))
                        End If
                    End If
                Next

                Interlocked.CompareExchange(m_nameToTypeMembersMap, dictionary, Nothing)
            End If

            Return m_nameToTypeMembersMap
        End Function

        Public Overloads Overrides Function GetMembers() As ImmutableArray(Of Symbol)
            If (m_lazyState And StateFlags.AllMembersIsSorted) <> 0 Then
                Return m_lazyAllMembers

            Else
                Dim allMembers = Me.GetMembersUnordered()

                If allMembers.Length >= 2 Then
                    allMembers = allMembers.Sort(LexicalOrderSymbolComparer.Instance)
                    ImmutableInterlocked.InterlockedExchange(m_lazyAllMembers, allMembers)
                End If

                ThreadSafeFlagOperations.Set(m_lazyState, StateFlags.AllMembersIsSorted)

                Return allMembers
            End If
        End Function

        Friend Overloads Overrides Function GetMembersUnordered() As ImmutableArray(Of Symbol)
            If m_lazyAllMembers.IsDefault Then
                Dim members = StaticCast(Of Symbol).From(Me.GetNameToMembersMap().Flatten())
                ImmutableInterlocked.InterlockedCompareExchange(m_lazyAllMembers, members, Nothing)
            End If

#If DEBUG Then
            ' In DEBUG, swap first and last elements so that use of Unordered in a place it isn't warranted is caught
            ' more obviously.
            Return m_lazyAllMembers.DeOrder()
#Else
            Return m_lazyAllMembers
#End If
        End Function

        Public Overloads Overrides Function GetMembers(name As String) As ImmutableArray(Of Symbol)
            Dim members As ImmutableArray(Of NamespaceOrTypeSymbol) = Nothing
            If Me.GetNameToMembersMap().TryGetValue(name, members) Then
                Return ImmutableArray.Create(Of Symbol, NamespaceOrTypeSymbol)(members)
            Else
                Return ImmutableArray(Of Symbol).Empty
            End If
        End Function

        Friend Overrides Function GetTypeMembersUnordered() As ImmutableArray(Of NamedTypeSymbol)
            Return Me.GetNameToTypeMembersMap().Flatten()
        End Function

        Public Overloads Overrides Function GetTypeMembers() As ImmutableArray(Of NamedTypeSymbol)
            Return Me.GetNameToTypeMembersMap().Flatten(LexicalOrderSymbolComparer.Instance)
        End Function

        Public Overloads Overrides Function GetTypeMembers(name As String) As ImmutableArray(Of NamedTypeSymbol)
            Dim members As ImmutableArray(Of NamedTypeSymbol) = Nothing
            If Me.GetNameToTypeMembersMap().TryGetValue(name, members) Then
                Return members
            Else
                Return ImmutableArray(Of NamedTypeSymbol).Empty
            End If
        End Function

        ' This is very performance critical for type lookup.
        Public Overrides Function GetModuleMembers() As ImmutableArray(Of NamedTypeSymbol)
            If m_lazyModuleMembers.IsDefault Then
                Dim moduleMembers = ArrayBuilder(Of NamedTypeSymbol).GetInstance()

                ' look at all child declarations to find the modules.
                For Each childDecl In m_declaration.Children
                    If childDecl.Kind = DeclarationKind.Module Then
                        moduleMembers.AddRange(GetModuleMembers(childDecl.Name))
                    End If
                Next

                ImmutableInterlocked.InterlockedCompareExchange(m_lazyModuleMembers,
                                                    moduleMembers.ToImmutableAndFree(),
                                                    Nothing)
            End If

            Return m_lazyModuleMembers
        End Function

        Friend Overrides Function GetLexicalSortKey() As LexicalSortKey
            ' WARNING: this should not allocate memory!
            If Not m_lazyLexicalSortKey.IsInitialized Then
                m_lazyLexicalSortKey.SetFrom(m_declaration.GetLexicalSortKey(Me.DeclaringCompilation))
            End If
            Return m_lazyLexicalSortKey
        End Function

        Public Overrides ReadOnly Property Locations As ImmutableArray(Of Location)
            Get
                Return StaticCast(Of Location).From(m_declaration.NameLocations)
            End Get
        End Property

        Public Overrides ReadOnly Property DeclaringSyntaxReferences As ImmutableArray(Of SyntaxReference)
            Get
                Dim declarations As ImmutableArray(Of SingleNamespaceDeclaration) = m_declaration.Declarations

                Dim builder As ArrayBuilder(Of SyntaxReference) = ArrayBuilder(Of SyntaxReference).GetInstance(declarations.Length)

                ' SyntaxReference in the namespace declaration points to the name node of the namespace decl node not
                ' namespace decl node we want to return. here we will wrap the original syntax reference in 
                ' the translation syntax reference so that we can lazily manipulate a node return to the caller
                For Each decl In declarations
                    Dim reference = decl.SyntaxReference
                    If reference IsNot Nothing AndAlso Not reference.SyntaxTree.IsEmbeddedOrMyTemplateTree() Then
                        builder.Add(New NamespaceDeclarationSyntaxReference(reference))
                    End If
                Next

                Return builder.ToImmutableAndFree()
            End Get
        End Property

        Friend Overrides Function IsDefinedInSourceTree(tree As SyntaxTree, definedWithinSpan As TextSpan?, Optional cancellationToken As CancellationToken = Nothing) As Boolean
            If Me.IsGlobalNamespace Then
                Return True
            Else
                ' Check if any namespace declaration block intersects with the given tree/span.
                Dim syntaxRefs = Me.DeclaringSyntaxReferences
                If syntaxRefs.Length = 0 Then
                    Return True
                End If

                For Each syntaxRef In syntaxRefs
                    cancellationToken.ThrowIfCancellationRequested()

                    Dim syntax = syntaxRef.GetSyntax(cancellationToken)
                    If TypeOf syntax Is NamespaceStatementSyntax Then
                        ' Get the parent NamespaceBlockSyntax
                        syntax = syntax.Parent
                    End If

                    If IsDefinedInSourceTree(syntax, tree, definedWithinSpan, cancellationToken) Then
                        Return True
                    End If
                Next

                Return False
            End If
        End Function

        ' Force all declaration errors to be generated
        Friend Overrides Sub GenerateDeclarationErrors(cancellationToken As CancellationToken)
            MyBase.GenerateDeclarationErrors(cancellationToken)

            ValidateDeclaration(Nothing, cancellationToken)

            ' Getting all the members will force declaration errors for contained stuff.
            GetMembers()
        End Sub

        ' Force all declaration errors In Tree to be generated
        Friend Sub GenerateDeclarationErrorsInTree(tree As SyntaxTree, filterSpanWithinTree As TextSpan?, cancellationToken As CancellationToken)

            ValidateDeclaration(tree, cancellationToken)

            ' Getting all the members will force declaration errors for contained stuff.
            GetMembers()
        End Sub

        ' Validate a namespace declaration. This is called for each namespace being declared, so 
        ' for example, it is called twice on Namespace X.Y, once with "X" and once with "X.Y".
        ' It will also be called with the CompilationUnit.
        Private Sub ValidateDeclaration(tree As SyntaxTree, cancellationToken As CancellationToken)
            If (m_lazyState And StateFlags.DeclarationValidated) <> 0 Then
                Return
            End If

            Dim diagnostics As DiagnosticBag = DiagnosticBag.GetInstance()
            Dim reportedNamespaceMismatch As Boolean = False

            ' Check for a few issues with namespace declaration.
            For Each syntaxRef In m_declaration.SyntaxReferences
                If tree IsNot Nothing AndAlso syntaxRef.SyntaxTree IsNot tree Then
                    Continue For
                End If

                Dim currentTree = syntaxRef.SyntaxTree
                Dim node As VisualBasicSyntaxNode = syntaxRef.GetVisualBasicSyntax()
                Select Case node.Kind
                    Case SyntaxKind.IdentifierName
                        ValidateNamespaceNameSyntax(DirectCast(node, IdentifierNameSyntax), diagnostics, reportedNamespaceMismatch)
                    Case SyntaxKind.QualifiedName
                        ValidateNamespaceNameSyntax(DirectCast(node, QualifiedNameSyntax).Right, diagnostics, reportedNamespaceMismatch)
                    Case SyntaxKind.GlobalName
                        ValidateNamespaceGlobalSyntax(DirectCast(node, GlobalNameSyntax), diagnostics)
                    Case SyntaxKind.CompilationUnit
                    ' nothing to validate
                    Case Else
                        Throw ExceptionUtilities.UnexpectedValue(node.Kind)
                End Select

                cancellationToken.ThrowIfCancellationRequested()
            Next

            If m_containingModule.AtomicSetFlagAndStoreDiagnostics(m_lazyState, StateFlags.DeclarationValidated, 0, diagnostics, CompilationStage.Declare) Then
                DeclaringCompilation.SymbolDeclaredEvent(Me)
            End If
            diagnostics.Free()
        End Sub

        ' Validate a particular namespace name.
        Private Sub ValidateNamespaceNameSyntax(node As SimpleNameSyntax, diagnostics As DiagnosticBag, ByRef reportedNamespaceMismatch As Boolean)
            If (node.Identifier.GetTypeCharacter() <> TypeCharacter.None) Then
                Dim diag = New VBDiagnostic(ErrorFactory.ErrorInfo(ERRID.ERR_TypecharNotallowed), node.GetLocation())
                diagnostics.Add(diag)
            End If

            ' Warning should only be reported for the first mismatch for each namespace to
            ' avoid reporting a large number of warnings in projects with many files.
            ' This is by design
            ' TODO: do we really want to omit these warnings and display a new one after each fix?
            ' VS can display errors and warnings separately in the IDE, so it may be ok to flood the users with
            ' these warnings.
            If Not reportedNamespaceMismatch AndAlso
                String.Compare(node.Identifier.ValueText, Me.Name, StringComparison.Ordinal) <> 0 Then
                ' all namespace names from the declarations match following the VB identifier comparison rules,
                ' so we just need to check when they are not matching using case sensitive comparison.

                ' filename is the one where the correct declaration occurred in Dev10
                ' TODO: report "related location" rather than including path in the message:
                Dim path = GetSourcePathForDeclaration()
                Dim diag = New VBDiagnostic(ErrorFactory.ErrorInfo(ERRID.WRN_NamespaceCaseMismatch3, node.Identifier.ValueText, Me.Name, path), node.GetLocation())
                diagnostics.Add(diag)
                reportedNamespaceMismatch = True
            End If

            ' TODO: once the declarations are sorted, one might cache the filename if the first declaration matches the case. 
            ' then GetFilenameForDeclaration is only needed if the mismatch occurs before any matching declaration.
        End Sub

        ' Validate that Global namespace name can't be nested inside another namespace.
        Private Sub ValidateNamespaceGlobalSyntax(node As GlobalNameSyntax, diagnostics As DiagnosticBag)
            Dim ancestorNode = node.Parent
            Dim seenNamespaceBlock As Boolean = False

            ' Go up the syntax hierarchy and make sure we only hit one namespace block (our own).
            While ancestorNode IsNot Nothing
                If ancestorNode.Kind = SyntaxKind.NamespaceBlock Then
                    If seenNamespaceBlock Then
                        ' Our namespace block is nested within another. That's a no-no.
                        Dim diag = New VBDiagnostic(ErrorFactory.ErrorInfo(ERRID.ERR_NestedGlobalNamespace), node.GetLocation())
                        diagnostics.Add(diag)
                    Else
                        seenNamespaceBlock = True
                    End If
                End If

                ancestorNode = ancestorNode.Parent
            End While
        End Sub

        ''' <summary>
        ''' Gets the filename of the first declaration that matches the given namespace name case sensitively.
        ''' </summary>
        Private Function GetSourcePathForDeclaration() As Object
            Debug.Assert(m_declaration.Declarations.Length > 0)

            ' unfortunately we cannot initialize with the filename of the first declaration because that filename might be nothing.
            Dim path = Nothing

            For Each declaration In m_declaration.Declarations
                If String.Compare(Me.Name, declaration.Name, StringComparison.Ordinal) = 0 Then
                    If declaration.IsPartOfRootNamespace Then
                        'path = StringConstants.ProjectSettingLocationName
                        path = New LocalizableErrorArgument(ERRID.IDS_ProjectSettingsLocationName)

                    ElseIf declaration.SyntaxReference IsNot Nothing AndAlso
                                                    declaration.SyntaxReference.SyntaxTree.FilePath IsNot Nothing Then

                        Dim otherPath = declaration.SyntaxReference.SyntaxTree.FilePath
                        If path Is Nothing Then
                            path = otherPath
                        ElseIf String.Compare(path.ToString, otherPath.ToString, StringComparison.Ordinal) > 0 Then
                            path = otherPath
                        End If
                    End If
                End If
            Next

            Return path
        End Function

        ''' <summary>
        ''' Return the set of types that should be checked for presence of extension methods in order to build
        ''' a map of extension methods for the namespace. 
        ''' </summary>
        Friend Overrides ReadOnly Property TypesToCheckForExtensionMethods As ImmutableArray(Of NamedTypeSymbol)
            Get
                If m_containingModule.MightContainExtensionMethods Then
                    ' Note that we are using GetModuleMembers because only Modules can contain extension methods in source.
                    Return Me.GetModuleMembers()
                End If

                Return ImmutableArray(Of NamedTypeSymbol).Empty
            End Get
        End Property

        ''' <summary>
        ''' Does this namespace have multiple different different case-sensitive spellings
        ''' (i.e., "Namespace FOO" and "Namespace foo". Includes parent namespace(s).
        ''' </summary>
        Friend ReadOnly Property HasMultipleSpellings As Boolean
            Get
                Return (m_lazyState And StateFlags.HasMultipleSpellings) <> 0
            End Get
        End Property


        ''' <summary>
        ''' Get the fully qualified namespace name using the spelling used in the declaration enclosing the given
        ''' syntax tree and location.
        ''' I.e., if this namespace was declared with:
        ''' Namespace zAp
        '''  Namespace FOO.bar
        '''    'location
        '''  End Namespace
        ''' End Namespace
        ''' Namespace ZAP
        '''  Namespace foo.bar
        '''  End Namespace
        ''' End Namespace
        ''' 
        ''' It would return "ProjectNamespace.zAp.FOO.bar".
        ''' </summary>
        Friend Function GetDeclarationSpelling(tree As SyntaxTree, location As Integer) As String
            If Not HasMultipleSpellings Then
                ' Only one spelling. Just return that.
                Return ToDisplayString(SymbolDisplayFormat.QualifiedNameOnlyFormat)
            Else
                ' Since the declaration builder has already resolved things like "Global", qualified names, etc, 
                ' just find the declaration that encloses the location (as opposed to recreating the name 
                ' by walking the syntax)
                Dim containingDecl = m_declaration.Declarations.FirstOrDefault(Function(decl)
                                                                                   Dim nsBlock As NamespaceBlockSyntax = decl.GetNamespaceBlockSyntax()
                                                                                   Return nsBlock IsNot Nothing AndAlso nsBlock.SyntaxTree Is tree AndAlso nsBlock.Span.Contains(location)
                                                                               End Function)
                If containingDecl Is Nothing Then
                    ' Could be project namespace, which has no namespace block syntax.
                    containingDecl = m_declaration.Declarations.FirstOrDefault(Function(decl) decl.GetNamespaceBlockSyntax() Is Nothing)
                End If

                Dim containingDeclName = If(containingDecl IsNot Nothing, containingDecl.Name, Me.Name)
                Dim containingNamespace = TryCast(Me.ContainingNamespace, SourceNamespaceSymbol)
                Dim fullDeclName As String
                If containingNamespace IsNot Nothing AndAlso containingNamespace.Name <> "" Then
                    fullDeclName = containingNamespace.GetDeclarationSpelling(tree, location) + "." + containingDeclName
                Else
                    fullDeclName = containingDeclName
                End If

                Debug.Assert(IdentifierComparison.Equals(fullDeclName, ToDisplayString(SymbolDisplayFormat.QualifiedNameOnlyFormat)))
                Return fullDeclName
            End If
        End Function

        Public ReadOnly Property MergedDeclaration As MergedNamespaceDeclaration
            Get
                Return m_declaration
            End Get
        End Property
    End Class
End Namespace
