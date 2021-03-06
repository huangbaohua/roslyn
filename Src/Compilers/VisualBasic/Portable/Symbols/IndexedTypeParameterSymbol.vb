﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System
Imports System.Collections.Generic
Imports System.Collections.Immutable
Imports System.Diagnostics
Imports System.Threading
Imports Microsoft.CodeAnalysis.Collections
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.Symbols

    ''' <summary>
    ''' Indexed type parameters are used in place of type parameters for method signatures.  There is
    ''' a unique mapping from index to a single IndexedTypeParameterSymbol.  
    ''' 
    ''' They don't have a containing symbol or locations.
    ''' 
    ''' They do not have constraints, variance, or attributes. 
    ''' </summary>
    Friend NotInheritable Class IndexedTypeParameterSymbol
        Inherits TypeParameterSymbol

        Private Shared parameterPool As TypeParameterSymbol() = SpecializedCollections.EmptyArray(Of TypeParameterSymbol)()

        Private ReadOnly index As Integer

        Private Sub New(index As Integer)
            Me.index = index
        End Sub

        Friend Shared Function GetTypeParameter(index As Integer) As TypeParameterSymbol
            If index >= parameterPool.Length Then
                GrowPool(index + 1)
            End If

            Return parameterPool(index)
        End Function

        Private Shared Sub GrowPool(count As Integer)
            Dim initialPool = parameterPool
            While count > initialPool.Length
                Dim newPoolSize = ((count + &HF) And Not &HF)
                Dim newPool = New TypeParameterSymbol(0 To newPoolSize - 1) {}

                Array.Copy(initialPool, newPool, initialPool.Length)

                For i As Integer = initialPool.Length To newPool.Length - 1
                    newPool(i) = New IndexedTypeParameterSymbol(i)
                Next

                Interlocked.CompareExchange(parameterPool, newPool, initialPool)

                ' repeat if race condition occurred and someone else resized the pool before us
                ' and the new pool is still too small

                initialPool = parameterPool
            End While

        End Sub

        ''' <summary>
        ''' Create a vector of n dummy type parameters.  Always reuses the same type parameter symbol
        ''' for the same position.
        ''' </summary>
        ''' <param name="count"></param>
        ''' <returns></returns>
        Friend Shared Function Take(count As Integer) As ImmutableArray(Of TypeParameterSymbol)
            If count > parameterPool.Length Then
                GrowPool(count)
            End If

            Dim builder As ArrayBuilder(Of TypeParameterSymbol) = ArrayBuilder(Of TypeParameterSymbol).GetInstance()
            For i = 0 To count - 1
                builder.Add(GetTypeParameter(i))
            Next i

            Return builder.ToImmutableAndFree()
        End Function

        Public Overrides ReadOnly Property TypeParameterKind As TypeParameterKind
            Get
                Return TypeParameterKind.Method
            End Get
        End Property

        Public Overrides ReadOnly Property Ordinal As Integer
            Get
                Return index
            End Get
        End Property

        ' These object are unique (per index).
        Public Overrides Function Equals(obj As Object) As Boolean
            Return Me Is obj
        End Function

        Public Overrides Function GetHashCode() As Integer
            Return index
        End Function

        Public Overrides ReadOnly Property Variance As VarianceKind
            Get
                Return VarianceKind.None
            End Get
        End Property

        Public Overrides ReadOnly Property HasValueTypeConstraint As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property HasReferenceTypeConstraint As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property HasConstructorConstraint As Boolean
            Get
                Return False
            End Get
        End Property

        Friend Overrides ReadOnly Property ConstraintTypesNoUseSiteDiagnostics As ImmutableArray(Of TypeSymbol)
            Get
                Return ImmutableArray(Of TypeSymbol).Empty
            End Get
        End Property

        Public Overrides ReadOnly Property ContainingSymbol As Symbol
            Get
                Return Nothing
            End Get
        End Property

        Public Overrides ReadOnly Property Locations As ImmutableArray(Of Location)
            Get
                Return ImmutableArray(Of Location).Empty
            End Get
        End Property

        Public Overrides ReadOnly Property DeclaringSyntaxReferences As ImmutableArray(Of SyntaxReference)
            Get
                Return ImmutableArray(Of SyntaxReference).Empty
            End Get
        End Property

        Friend Overrides Sub EnsureAllConstraintsAreResolved()
        End Sub

    End Class

End Namespace

