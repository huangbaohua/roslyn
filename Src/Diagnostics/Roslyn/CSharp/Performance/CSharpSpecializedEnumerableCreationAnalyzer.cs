﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Roslyn.Diagnostics.Analyzers.CSharp
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class CSharpSpecializedEnumerableCreationAnalyzer : SpecializedEnumerableCreationAnalyzer
    {
        protected override void GetCodeBlockStartedAnalyzer(CompilationStartAnalysisContext context, INamedTypeSymbol genericEnumerableSymbol, IMethodSymbol genericEmptyEnumerableSymbol)
        {
            context.RegisterCodeBlockStartAction<SyntaxKind>(new CodeBlockStartedAnalyzer(genericEnumerableSymbol, genericEmptyEnumerableSymbol).Initialize);
        }

        private sealed class CodeBlockStartedAnalyzer : AbstractCodeBlockStartedAnalyzer<SyntaxKind>
        {
            public CodeBlockStartedAnalyzer(INamedTypeSymbol genericEnumerableSymbol, IMethodSymbol genericEmptyEnumerableSymbol) :
                base(genericEnumerableSymbol, genericEmptyEnumerableSymbol)
            {
            }

            protected override void GetSyntaxAnalyzer(CodeBlockStartAnalysisContext<SyntaxKind> context, INamedTypeSymbol genericEnumerableSymbol, IMethodSymbol genericEmptyEnumerableSymbol)
            {
                context.RegisterSyntaxNodeAction(new SyntaxAnalyzer(genericEnumerableSymbol, genericEmptyEnumerableSymbol).AnalyzeNode, SyntaxKind.ReturnStatement);
            }
        }

        private sealed class SyntaxAnalyzer : AbstractSyntaxAnalyzer
        {
            public SyntaxAnalyzer(INamedTypeSymbol genericEnumerableSymbol, IMethodSymbol genericEmptyEnumerableSymbol) :
                base(genericEnumerableSymbol, genericEmptyEnumerableSymbol)
            {
            }

            public void AnalyzeNode(SyntaxNodeAnalysisContext context)
            {
                var expressionsToAnalyze = context.Node.DescendantNodes().Where(n => ShouldAnalyzeExpression(n, context.SemanticModel));

                foreach (var expression in expressionsToAnalyze)
                {
                    switch (expression.Kind())
                    {
                        case SyntaxKind.ArrayCreationExpression:
                            AnalyzeArrayCreationExpression((ArrayCreationExpressionSyntax)expression, context.ReportDiagnostic);
                            break;
                        case SyntaxKind.ImplicitArrayCreationExpression:
                            AnalyzeInitializerExpression(((ImplicitArrayCreationExpressionSyntax)expression).Initializer, context.ReportDiagnostic);
                            break;
                        case SyntaxKind.SimpleMemberAccessExpression:
                            AnalyzeMemberAccessName(((MemberAccessExpressionSyntax)expression).Name, context.SemanticModel, context.ReportDiagnostic);
                            break;
                    }
                }
            }

            private bool ShouldAnalyzeExpression(SyntaxNode expression, SemanticModel semanticModel)
            {
                switch (expression.Kind())
                {
                    case SyntaxKind.ArrayCreationExpression:
                    case SyntaxKind.ImplicitArrayCreationExpression:
                        return ShouldAnalyzeArrayCreationExpression(expression, semanticModel);
                    case SyntaxKind.SimpleMemberAccessExpression:
                        return true;
                    default:
                        return false;
                }
            }

            private static void AnalyzeArrayCreationExpression(ArrayCreationExpressionSyntax arrayCreationExpression, Action<Diagnostic> addDiagnostic)
            {
                var arrayType = arrayCreationExpression.Type;
                if (arrayType.RankSpecifiers.Count == 1)
                {
                    // Check for explicit specification of empty or singleton array
                    var literalRankSpecifier = arrayType.RankSpecifiers[0].ChildNodes()
                        .SingleOrDefault(n => n.Kind() == SyntaxKind.NumericLiteralExpression)
                        as LiteralExpressionSyntax;

                    if (literalRankSpecifier != null)
                    {
                        Debug.Assert(literalRankSpecifier.Token.Value != null);
                        AnalyzeArrayLength((int)literalRankSpecifier.Token.Value, arrayCreationExpression, addDiagnostic);
                        return;
                    }
                }

                AnalyzeInitializerExpression(arrayCreationExpression.Initializer, addDiagnostic);
            }

            private static void AnalyzeInitializerExpression(InitializerExpressionSyntax initializer, Action<Diagnostic> addDiagnostic)
            {
                // Check length of initializer list for empty or singleton array
                if (initializer != null)
                {
                    AnalyzeArrayLength(initializer.Expressions.Count, initializer.Parent, addDiagnostic);
                }
            }
        }
    }
}
