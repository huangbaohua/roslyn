﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// A RangeVariableSymbol represents an identifier introduced in a query expression as the
    /// identifier of a "from" clause, an "into" query continuation, a "let" clause, or a "join" clause.
    /// </summary>
    internal class RangeVariableSymbol : Symbol, IRangeVariableSymbol
    {
        private readonly string name;
        private readonly ImmutableArray<Location> locations;
        private readonly Symbol containingSymbol;

        internal RangeVariableSymbol(string Name, Symbol containingSymbol, Location location, bool isTransparent = false)
        {
            this.name = Name;
            this.containingSymbol = containingSymbol;
            this.locations = ImmutableArray.Create<Location>(location);
            this.IsTransparent = isTransparent;
        }

        internal bool IsTransparent { get; private set; }

        public override string Name
        {
            get
            {
                return name;
            }
        }

        public override SymbolKind Kind
        {
            get
            {
                return SymbolKind.RangeVariable;
            }
        }

        public override ImmutableArray<Location> Locations
        {
            get
            {
                return locations;
            }
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get
            {
                SyntaxToken token = (SyntaxToken)locations[0].SourceTree.GetRoot().FindToken(locations[0].SourceSpan.Start);
                Debug.Assert(token.Kind() == SyntaxKind.IdentifierToken);
                CSharpSyntaxNode node = (CSharpSyntaxNode)token.Parent;
                Debug.Assert(node is QueryClauseSyntax || node is QueryContinuationSyntax || node is JoinIntoClauseSyntax);
                return ImmutableArray.Create<SyntaxReference>(node.GetReference());
            }
        }

        public override bool IsExtern
        {
            get
            {
                return false;
            }
        }

        public override bool IsSealed
        {
            get
            {
                return false;
            }
        }

        public override bool IsAbstract
        {
            get
            {
                return false;
            }
        }

        public override bool IsOverride
        {
            get
            {
                return false;
            }
        }

        public override bool IsVirtual
        {
            get
            {
                return false;
            }
        }

        public override bool IsStatic
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Returns data decoded from Obsolete attribute or null if there is no Obsolete attribute.
        /// This property returns ObsoleteAttributeData.Uninitialized if attribute arguments haven't been decoded yet.
        /// </summary>
        internal sealed override ObsoleteAttributeData ObsoleteAttributeData
        {
            get { return null; }
        }

        public override Accessibility DeclaredAccessibility
        {
            get
            {
                return Accessibility.NotApplicable;
            }
        }

        public override Symbol ContainingSymbol
        {
            get
            {
                return containingSymbol;
            }
        }

        public override void Accept(SymbolVisitor visitor)
        {
            visitor.VisitRangeVariable(this);
        }

        public override TResult Accept<TResult>(SymbolVisitor<TResult> visitor)
        {
            return visitor.VisitRangeVariable(this);
        }

        internal override TResult Accept<TArg, TResult>(CSharpSymbolVisitor<TArg, TResult> visitor, TArg a)
        {
            return visitor.VisitRangeVariable(this, a);
        }

        public override void Accept(CSharpSymbolVisitor visitor)
        {
            visitor.VisitRangeVariable(this);
        }

        public override TResult Accept<TResult>(CSharpSymbolVisitor<TResult> visitor)
        {
            return visitor.VisitRangeVariable(this);
        }

        public override bool Equals(object obj)
        {
            if (obj == (object)this)
            {
                return true;
            }

            var symbol = obj as RangeVariableSymbol;
            return (object)symbol != null
                && symbol.locations[0].Equals(this.locations[0])
                && Equals(containingSymbol, symbol.ContainingSymbol);
        }

        public override int GetHashCode()
        {
            return Hash.Combine(locations[0].GetHashCode(), containingSymbol.GetHashCode());
        }
    }
}
