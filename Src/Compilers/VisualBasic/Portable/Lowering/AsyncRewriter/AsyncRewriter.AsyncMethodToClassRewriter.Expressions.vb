﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.Generic
Imports System.Collections.Immutable
Imports System.Runtime.InteropServices
Imports System.Threading
Imports Microsoft.Cci
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.Collections
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports TypeKind = Microsoft.CodeAnalysis.TypeKind

Namespace Microsoft.CodeAnalysis.VisualBasic

    Partial Friend NotInheritable Class AsyncRewriter
        Inherits StateMachineRewriter(Of CapturedSymbolOrExpression)

        Partial Friend Class AsyncMethodToClassRewriter
            Inherits StateMachineMethodToClassRewriter

            Public Function VisitExpression(expression As BoundExpression) As BoundExpression
                Return DirectCast(Me.Visit(expression), BoundExpression)
            End Function

            Public Overrides Function VisitSpillSequence(node As BoundSpillSequence) As BoundNode
                Dim statements As ImmutableArray(Of BoundStatement) = Me.VisitList(node.Statements)
                Dim valueOpt As BoundExpression = Me.VisitExpression(node.ValueOpt)
                Dim rewrittenType As TypeSymbol = VisitType(node.Type)

                If valueOpt Is Nothing OrElse valueOpt.Kind <> BoundKind.SpillSequence Then
                    Return node.Update(node.Locals, node.SpillFields, statements, valueOpt, rewrittenType)
                End If

                Dim spillSeq = DirectCast(valueOpt, BoundSpillSequence)
                Debug.Assert(rewrittenType = spillSeq.Type)

                Return node.Update(
                    node.Locals.Concat(spillSeq.Locals),
                    node.SpillFields.Concat(spillSeq.SpillFields),
                    statements.Concat(spillSeq.Statements),
                    spillSeq.ValueOpt,
                    rewrittenType)
            End Function

            Public Overrides Function VisitSequence(node As BoundSequence) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitSequence(node), BoundSequence)
                Dim locals As ImmutableArray(Of LocalSymbol) = rewritten.Locals
                Dim sideEffects As ImmutableArray(Of BoundExpression) = rewritten.SideEffects
                Dim valueOpt As BoundExpression = rewritten.ValueOpt

                Dim sideEffectsRequireSpill As Boolean = NeedsSpill(sideEffects)
                Dim valueRequiresSpill As Boolean = NeedsSpill(valueOpt)

                If Not sideEffectsRequireSpill AndAlso Not valueRequiresSpill Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()

                builder.AddLocals(locals)

                If sideEffectsRequireSpill Then
                    For Each sideEffect In sideEffects
                        builder.AddStatement(MakeExpressionStatement(sideEffect, builder))
                    Next
                End If

                If valueRequiresSpill Then
                    Dim spill = DirectCast(valueOpt, BoundSpillSequence)
                    builder.AddSpill(spill)
                    valueOpt = spill.ValueOpt
                End If

                Return builder.BuildSequenceAndFree(Me.F, valueOpt)
            End Function

            Private Function MakeExpressionStatement(expression As BoundExpression, ByRef builder As SpillBuilder) As BoundStatement
                If NeedsSpill(expression) Then
                    Debug.Assert(expression.Kind = BoundKind.SpillSequence)
                    Dim spill = DirectCast(expression, BoundSpillSequence)
                    builder.AssumeFieldsIfNeeded(spill)
                    Return Me.RewriteSpillSequenceIntoBlock(spill, True)
                Else
                    Return Me.F.ExpressionStatement(expression)
                End If
            End Function

            Public Overrides Function VisitCall(node As BoundCall) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitCall(node), BoundCall)
                Dim receiverOpt As BoundExpression = rewritten.ReceiverOpt
                Dim arguments As ImmutableArray(Of BoundExpression) = rewritten.Arguments

                If Not NeedsSpill(arguments) AndAlso Not NeedsSpill(receiverOpt) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()

                Dim result = SpillExpressionsWithReceiver(receiverOpt, isReceiverOfAMethodCall:=True, expressions:=arguments, spillBuilder:=builder)

                Return builder.BuildSequenceAndFree(Me.F,
                                                    rewritten.Update(rewritten.Method,
                                                                     rewritten.MethodGroupOpt,
                                                                     result.ReceiverOpt,
                                                                     result.Arguments,
                                                                     rewritten.ConstantValueOpt,
                                                                     rewritten.SuppressObjectClone,
                                                                     rewritten.Type))
            End Function

            Public Overrides Function VisitObjectCreationExpression(node As BoundObjectCreationExpression) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitObjectCreationExpression(node), BoundObjectCreationExpression)
                Dim arguments As ImmutableArray(Of BoundExpression) = rewritten.Arguments
                Debug.Assert(rewritten.InitializerOpt Is Nothing)

                If Not NeedsSpill(arguments) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()
                arguments = SpillExpressionList(builder, arguments, firstArgumentIsAReceiverOfAMethodCall:=False)

                Return builder.BuildSequenceAndFree(Me.F,
                                                    rewritten.Update(rewritten.ConstructorOpt,
                                                                     arguments,
                                                                     rewritten.InitializerOpt,
                                                                     rewritten.Type))
            End Function

            Public Overrides Function VisitDelegateCreationExpression(node As BoundDelegateCreationExpression) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitDelegateCreationExpression(node), BoundDelegateCreationExpression)
                Dim receiverOpt As BoundExpression = rewritten.ReceiverOpt
                Debug.Assert(rewritten.RelaxationLambdaOpt Is Nothing)
                Debug.Assert(rewritten.RelaxationReceiverPlaceholderOpt Is Nothing)

                If Not NeedsSpill(receiverOpt) Then
                    Return rewritten
                End If

                Debug.Assert(receiverOpt.Kind = BoundKind.SpillSequence)
                Dim spill = DirectCast(receiverOpt, BoundSpillSequence)
                Return SpillSequenceWithNewValue(spill,
                                                 rewritten.Update(spill.ValueOpt,
                                                                  rewritten.Method,
                                                                  rewritten.RelaxationLambdaOpt,
                                                                  rewritten.RelaxationReceiverPlaceholderOpt,
                                                                  rewritten.MethodGroupOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitBinaryOperator(node As BoundBinaryOperator) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitBinaryOperator(node), BoundBinaryOperator)
                Dim left As BoundExpression = rewritten.Left
                Dim right As BoundExpression = rewritten.Right

                If Not NeedsSpill(left) AndAlso Not NeedsSpill(right) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()
                If rewritten.OperatorKind = BinaryOperatorKind.AndAlso OrElse rewritten.OperatorKind = BinaryOperatorKind.OrElse Then
                    ' NOTE: Short circuit operators need to evaluate the right optionally
                    Dim spilledLeft = SpillValue(left, builder)

                    Dim tempLocal As LocalSymbol = Me.F.SynthesizedLocal(rewritten.Type)
                    builder.AddLocal(tempLocal)

                    builder.AddStatement(
                        If(rewritten.OperatorKind = BinaryOperatorKind.AndAlso,
                           Me.F.If(condition:=spilledLeft,
                                   thenClause:=MakeAssignmentStatement(right, tempLocal, builder),
                                   elseClause:=MakeAssignmentStatement(Me.F.Literal(False), tempLocal)),
                           Me.F.If(condition:=spilledLeft,
                                   thenClause:=MakeAssignmentStatement(Me.F.Literal(True), tempLocal),
                                   elseClause:=MakeAssignmentStatement(right, tempLocal, builder))))

                    Return builder.BuildSequenceAndFree(Me.F,
                                                        Me.F.Local(tempLocal, False))
                Else
                    ' Regular binary operator
                    Dim newArgs As ImmutableArray(Of BoundExpression) = SpillExpressionList(builder, left, right)
                    Return builder.BuildSequenceAndFree(Me.F,
                                                        rewritten.Update(rewritten.OperatorKind,
                                                                         newArgs(0),
                                                                         newArgs(1),
                                                                         rewritten.Checked,
                                                                         rewritten.ConstantValueOpt,
                                                                         rewritten.Type))
                End If
            End Function

            Public Overrides Function VisitAssignmentOperator(node As BoundAssignmentOperator) As BoundNode
                Return ProcessRewrittenAssignmentOperator(DirectCast(MyBase.VisitAssignmentOperator(node), BoundAssignmentOperator))
            End Function

            Friend Function ProcessRewrittenAssignmentOperator(rewritten As BoundAssignmentOperator) As BoundExpression
                Debug.Assert(rewritten.LeftOnTheRightOpt Is Nothing)

                Dim left As BoundExpression = rewritten.Left
                Dim right As BoundExpression = rewritten.Right

                Dim leftRequiresSpill As Boolean = NeedsSpill(left)
                Dim rightRequiresSpill As Boolean = NeedsSpill(right)

                If Not leftRequiresSpill AndAlso Not rightRequiresSpill Then
                    Return rewritten
                End If

                If Not rightRequiresSpill Then
                    ' only lvalue contains await
                    '     Spill(l_sideEffects, lvalue) = rvalue
                    ' is rewritten as:
                    '     Spill(l_sideEffects, lvalue = rvalue)
                    Debug.Assert(left.Kind = BoundKind.SpillSequence)
                    Dim spillSequence = DirectCast(left, BoundSpillSequence)
                    Return SpillSequenceWithNewValue(spillSequence,
                                                     rewritten.Update(spillSequence.ValueOpt,
                                                                      rewritten.LeftOnTheRightOpt,
                                                                      right,
                                                                      rewritten.SuppressObjectClone,
                                                                      rewritten.Type))

                End If

                Dim builder As New SpillBuilder()

                Debug.Assert(left.IsLValue)
                Dim spilledLeft As BoundExpression = SpillLValue(left, isReceiver:=False, builder:=builder)

                Dim rightAsSpillSequence = DirectCast(right, BoundSpillSequence)
                builder.AddSpill(rightAsSpillSequence)
                Return builder.BuildSequenceAndFree(Me.F,
                                                    rewritten.Update(spilledLeft,
                                                                     rewritten.LeftOnTheRightOpt,
                                                                     rightAsSpillSequence.ValueOpt,
                                                                     rewritten.SuppressObjectClone,
                                                                     rewritten.Type))
            End Function

            Public Overrides Function VisitReferenceAssignment(node As BoundReferenceAssignment) As BoundNode
                Dim origByRefLocal As BoundLocal = node.ByRefLocal
                Dim local As LocalSymbol = origByRefLocal.LocalSymbol
                Dim rewrittenType As TypeSymbol = VisitType(node.Type)

                If Not Me.Proxies.ContainsKey(local) Then
                    ' ByRef local was not captured
                    Dim rewrittenLeft As BoundLocal = DirectCast(Me.VisitExpression(origByRefLocal), BoundLocal)

                    Dim rewrittenRight As BoundExpression = Me.VisitExpression(node.LValue)
                    Debug.Assert(rewrittenRight.IsLValue)
                    Dim rightRequiresSpill As Boolean = NeedsSpill(rewrittenRight)

                    If Not rightRequiresSpill Then
                        Return node.Update(rewrittenLeft, rewrittenRight, node.IsLValue, rewrittenType)

                    Else
                        ' The right is spilled, but the left is still a ByRef local
                        ' Rewrite 
                        '       ReferenceAssignment(<by-ref-local> = Spill( ..., <l-value> ) )
                        ' Into 
                        '       Spill( ..., ReferenceAssignment( <by-ref-local>, <l-value> ) )

                        Debug.Assert(rewrittenRight.Kind = BoundKind.SpillSequence)
                        Dim rightSpill = DirectCast(rewrittenRight, BoundSpillSequence)

                        Return SpillSequenceWithNewValue(rightSpill,
                                                         node.Update(rewrittenLeft,
                                                                     rightSpill.ValueOpt,
                                                                     node.IsLValue,
                                                                     rewrittenType))
                    End If
                End If

                ' Here we have an assignment expression that is initializing a ref 
                ' local variable, and the ref local variable is to be lifted.
                Dim capturedLocal As CapturedSymbolOrExpression = Me.Proxies(local)

                ' This builder will collect initializers for the captured local, 
                ' these initializers are supposed to make sure all the parts of 
                ' the capture are properly assigned
                Dim initializersBuilder = ArrayBuilder(Of BoundExpression).GetInstance()
                capturedLocal.CreateCaptureInitializationCode(Me, initializersBuilder)

                Dim materializedCapture As BoundExpression = capturedLocal.Materialize(Me, node.IsLValue)

                ' Now... we actually do not need to rewrite or process 'right' in any way
                ' because we must have captured its essential parts in the local's capture.
                ' So we just create a sequence with these parts 

                If initializersBuilder.Count = 0 Then
                    initializersBuilder.Free()
                    Return materializedCapture
                End If

                initializersBuilder.Add(materializedCapture)
                Return Me.F.Sequence(initializersBuilder.ToArrayAndFree)
            End Function

            Public Overrides Function VisitFieldAccess(node As BoundFieldAccess) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitFieldAccess(node), BoundFieldAccess)
                Dim receiverOpt As BoundExpression = rewritten.ReceiverOpt

                If Not NeedsSpill(receiverOpt) Then
                    Return rewritten
                End If

                Debug.Assert(receiverOpt.Kind = BoundKind.SpillSequence)
                Dim spillSequence = DirectCast(receiverOpt, BoundSpillSequence)

                Return SpillSequenceWithNewValue(spillSequence,
                                                 rewritten.Update(spillSequence.ValueOpt,
                                                                  rewritten.FieldSymbol,
                                                                  rewritten.IsLValue,
                                                                  rewritten.SuppressVirtualCalls,
                                                                  rewritten.ConstantsInProgressOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitDirectCast(node As BoundDirectCast) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitDirectCast(node), BoundDirectCast)
                Dim operand As BoundExpression = rewritten.Operand
                Debug.Assert(rewritten.RelaxationLambdaOpt Is Nothing)

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spillSequence = DirectCast(operand, BoundSpillSequence)

                Return SpillSequenceWithNewValue(spillSequence,
                                                 rewritten.Update(spillSequence.ValueOpt,
                                                                  rewritten.ConversionKind,
                                                                  rewritten.SuppressVirtualCalls,
                                                                  rewritten.ConstantValueOpt,
                                                                  rewritten.RelaxationLambdaOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitTryCast(node As BoundTryCast) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitTryCast(node), BoundTryCast)
                Dim operand As BoundExpression = rewritten.Operand
                Debug.Assert(rewritten.RelaxationLambdaOpt Is Nothing)

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spillSequence = DirectCast(operand, BoundSpillSequence)

                Return SpillSequenceWithNewValue(spillSequence,
                                                 rewritten.Update(spillSequence.ValueOpt,
                                                                  rewritten.ConversionKind,
                                                                  rewritten.ConstantValueOpt,
                                                                  rewritten.RelaxationLambdaOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitConversion(node As BoundConversion) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitConversion(node), BoundConversion)
                Dim operand As BoundExpression = rewritten.Operand
                Debug.Assert(rewritten.RelaxationReceiverPlaceholderOpt Is Nothing)
                Debug.Assert(rewritten.RelaxationLambdaOpt Is Nothing)

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spillSequence = DirectCast(operand, BoundSpillSequence)

                Return SpillSequenceWithNewValue(spillSequence,
                                                 rewritten.Update(spillSequence.ValueOpt,
                                                                  rewritten.ConversionKind,
                                                                  rewritten.Checked,
                                                                  rewritten.ExplicitCastInCode,
                                                                  rewritten.ConstantValueOpt,
                                                                  rewritten.ConstructorOpt,
                                                                  rewritten.RelaxationLambdaOpt,
                                                                  rewritten.RelaxationReceiverPlaceholderOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitLValueToRValueWrapper(node As BoundLValueToRValueWrapper) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitLValueToRValueWrapper(node), BoundLValueToRValueWrapper)
                Debug.Assert(Not rewritten.IsLValue)

                Dim operand As BoundExpression = rewritten.UnderlyingLValue
                Debug.Assert(operand.IsLValue)

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spillSequence = DirectCast(operand, BoundSpillSequence)

                Return SpillSequenceWithNewValue(spillSequence,
                                                 rewritten.Update(spillSequence.ValueOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitTernaryConditionalExpression(node As BoundTernaryConditionalExpression) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitTernaryConditionalExpression(node), BoundTernaryConditionalExpression)
                Dim condition As BoundExpression = rewritten.Condition
                Dim whenTrue As BoundExpression = rewritten.WhenTrue
                Dim whenFalse As BoundExpression = rewritten.WhenFalse

                Dim conditionRequiresSpill As Boolean = NeedsSpill(condition)

                If Not conditionRequiresSpill AndAlso Not NeedsSpill(whenTrue) AndAlso Not NeedsSpill(whenFalse) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()

                If conditionRequiresSpill Then
                    Debug.Assert(condition.Kind = BoundKind.SpillSequence)
                    condition = SpillRValue(condition, builder)
                End If

                Dim sequenceValueOpt As BoundExpression

                If Not rewritten.Type.IsVoidType() Then
                    Dim tempLocal As LocalSymbol = Me.F.SynthesizedLocal(rewritten.Type)

                    builder.AddLocal(tempLocal)

                    builder.AddStatement(
                        Me.F.If(
                            condition:=condition,
                            thenClause:=MakeAssignmentStatement(whenTrue, tempLocal, builder),
                            elseClause:=MakeAssignmentStatement(whenFalse, tempLocal, builder)))

                    sequenceValueOpt = Me.F.Local(tempLocal, False)
                Else
                    builder.AddStatement(
                        Me.F.If(
                            condition:=condition,
                            thenClause:=MakeExpressionStatement(whenTrue, builder),
                            elseClause:=MakeExpressionStatement(whenFalse, builder)))

                    sequenceValueOpt = Nothing
                End If

                Return builder.BuildSequenceAndFree(Me.F, sequenceValueOpt)
            End Function

            Private Function MakeAssignmentStatement(expression As BoundExpression, temp As LocalSymbol, <[In], Out> ByRef builder As SpillBuilder) As BoundStatement
                If NeedsSpill(expression) Then
                    Debug.Assert(expression.Kind = BoundKind.SpillSequence)
                    Dim spill = DirectCast(expression, BoundSpillSequence)
                    builder.AssumeFieldsIfNeeded(spill)
                    Return RewriteSpillSequenceIntoBlock(spill, False, Me.F.Assignment(Me.F.Local(temp, True), spill.ValueOpt))
                Else
                    Return Me.F.Assignment(Me.F.Local(temp, True), expression)
                End If
            End Function

            Private Function MakeAssignmentStatement(expression As BoundExpression, temp As LocalSymbol) As BoundStatement
                Debug.Assert(Not NeedsSpill(expression))
                Return Me.F.Assignment(Me.F.Local(temp, True), expression)
            End Function

            Private Class ConditionalAccessReceiverPlaceholderReplacementInfo
                Public ReadOnly PlaceholderId As Integer
                Public IsSpilled As Boolean

                Public Sub New(placeholderId As Integer)
                    Me.PlaceholderId = placeholderId
                    Me.IsSpilled = False
                End Sub

            End Class

            Private m_ConditionalAccessReceiverPlaceholderReplacementInfo As ConditionalAccessReceiverPlaceholderReplacementInfo = Nothing

            Public Overrides Function VisitLoweredConditionalAccess(node As BoundLoweredConditionalAccess) As BoundNode
                Dim type As TypeSymbol = Me.VisitType(node.Type)

                Dim receiverOrCondition As BoundExpression = DirectCast(Me.Visit(node.ReceiverOrCondition), BoundExpression)
                Dim receiverOrConditionNeedsSpill = NeedsSpill(receiverOrCondition)

                Dim saveConditionalAccessReceiverPlaceholderReplacementInfo = m_ConditionalAccessReceiverPlaceholderReplacementInfo
                Dim conditionalAccessReceiverPlaceholderReplacementInfo As ConditionalAccessReceiverPlaceholderReplacementInfo

                If node.PlaceholderId <> 0 Then
                    conditionalAccessReceiverPlaceholderReplacementInfo = New ConditionalAccessReceiverPlaceholderReplacementInfo(node.PlaceholderId)
                Else
                    conditionalAccessReceiverPlaceholderReplacementInfo = Nothing
                End If

                m_ConditionalAccessReceiverPlaceholderReplacementInfo = conditionalAccessReceiverPlaceholderReplacementInfo

                Dim whenNotNull As BoundExpression = DirectCast(Me.Visit(node.WhenNotNull), BoundExpression)
                Dim whenNotNullNeedsSpill = NeedsSpill(whenNotNull)

                Debug.Assert(conditionalAccessReceiverPlaceholderReplacementInfo Is Nothing OrElse
                             (Not conditionalAccessReceiverPlaceholderReplacementInfo.IsSpilled OrElse whenNotNullNeedsSpill))

                m_ConditionalAccessReceiverPlaceholderReplacementInfo = Nothing

                Dim whenNullOpt As BoundExpression = DirectCast(Me.Visit(node.WhenNullOpt), BoundExpression)
                Dim whenNullNeedsSpill = If(whenNullOpt IsNot Nothing, NeedsSpill(whenNullOpt), False)

                m_ConditionalAccessReceiverPlaceholderReplacementInfo = saveConditionalAccessReceiverPlaceholderReplacementInfo

                If Not receiverOrConditionNeedsSpill AndAlso Not whenNotNullNeedsSpill AndAlso Not whenNullNeedsSpill Then
                    Return node.Update(receiverOrCondition,
                                       node.CaptureReceiver,
                                       node.PlaceholderId,
                                       whenNotNull,
                                       whenNullOpt,
                                       type)
                End If

                If Not whenNotNullNeedsSpill AndAlso Not whenNullNeedsSpill Then
                    Debug.Assert(receiverOrConditionNeedsSpill)
                    Dim spill = DirectCast(receiverOrCondition, BoundSpillSequence)
                    Return SpillSequenceWithNewValue(spill, node.Update(spill.ValueOpt,
                                                                        node.CaptureReceiver,
                                                                        node.PlaceholderId,
                                                                        whenNotNull,
                                                                        whenNullOpt,
                                                                        type))
                End If

                Dim builder As New SpillBuilder()

                If receiverOrConditionNeedsSpill Then
                    Dim spill = DirectCast(receiverOrCondition, BoundSpillSequence)
                    builder.AddSpill(spill)
                    receiverOrCondition = spill.ValueOpt
                End If

                If conditionalAccessReceiverPlaceholderReplacementInfo IsNot Nothing Then
                    ' We need to revisit the whenNotNull expression to replace placeholder

                    If node.CaptureReceiver OrElse conditionalAccessReceiverPlaceholderReplacementInfo.IsSpilled Then
                        ' Let's use stack spilling to capture it.
                        receiverOrCondition = SpillValue(receiverOrCondition, isReceiver:=True, builder:=builder)
                    End If

                    Dim rewriter As New ConditionalAccessReceiverPlaceholderReplacement(node.PlaceholderId, receiverOrCondition)

                    whenNotNull = DirectCast(rewriter.Visit(whenNotNull), BoundExpression)
                    Debug.Assert(rewriter.Replaced)
                End If

                If Not receiverOrCondition.Type.IsBooleanType() Then
                    ' We need to a add a null check for the receiver
                    If receiverOrCondition.Type.IsReferenceType Then
                        receiverOrCondition = Me.F.ReferenceIsNotNothing(receiverOrCondition.MakeRValue())
                    Else
                        Debug.Assert(Not receiverOrCondition.Type.IsValueType)
                        Debug.Assert(receiverOrCondition.Type.IsTypeParameter())

                        ' The "receiver IsNot Nothing" check becomes
                        ' Not <receiver's type is refernce type> OrElse receiver IsNot Nothing 
                        ' The <receiver's type is refernce type> is performed by boxing default value of receiver's type and checking if it is a null reference. 

                        Dim notReferenceType = Me.F.ReferenceIsNotNothing(Me.F.DirectCast(Me.F.DirectCast(Me.F.Null(),
                                                                                                          receiverOrCondition.Type),
                                                                                          Me.F.SpecialType(SpecialType.System_Object)))

                        receiverOrCondition = Me.F.LogicalOrElse(notReferenceType,
                                                                 Me.F.ReferenceIsNotNothing(Me.F.DirectCast(receiverOrCondition.MakeRValue(),
                                                                                                            Me.F.SpecialType(SpecialType.System_Object))))
                    End If
                End If

                If whenNullOpt Is Nothing Then
                    Debug.Assert(type.IsVoidType())
                    builder.AddStatement(
                    Me.F.If(condition:=receiverOrCondition,
                            thenClause:=MakeExpressionStatement(whenNotNull, builder)))

                    Return builder.BuildSequenceAndFree(Me.F, expression:=Nothing)
                Else
                    Debug.Assert(Not type.IsVoidType())
                    Dim tempLocal As LocalSymbol = Me.F.SynthesizedLocal(type)

                    builder.AddLocal(tempLocal)

                    builder.AddStatement(Me.F.If(condition:=receiverOrCondition,
                                                 thenClause:=MakeAssignmentStatement(whenNotNull, tempLocal, builder),
                                                 elseClause:=MakeAssignmentStatement(whenNullOpt, tempLocal, builder)))

                    Return builder.BuildSequenceAndFree(Me.F, expression:=Me.F.Local(tempLocal, False))
                End If
            End Function

            Private Class ConditionalAccessReceiverPlaceholderReplacement
                Inherits BoundTreeRewriter

                Private ReadOnly m_PlaceholderId As Integer
                Private ReadOnly m_ReplaceWith As BoundExpression
                Private m_Replaced As Boolean

                Public Sub New(placeholderId As Integer, replaceWith As BoundExpression)
                    Me.m_PlaceholderId = placeholderId
                    Me.m_ReplaceWith = replaceWith
                End Sub

                Public ReadOnly Property Replaced As Boolean
                    Get
                        Return m_Replaced
                    End Get
                End Property

                Public Overrides Function VisitConditionalAccessReceiverPlaceholder(node As BoundConditionalAccessReceiverPlaceholder) As BoundNode
                    Debug.Assert(m_PlaceholderId = node.PlaceholderId)

                    If m_PlaceholderId = node.PlaceholderId Then
                        Debug.Assert(Not m_Replaced)
                        m_Replaced = True

                        Dim result = m_ReplaceWith

                        If node.IsLValue Then
                            Debug.Assert(result.IsLValue)
                            Return result
                        Else
                            Return result.MakeRValue()
                        End If
                    End If

                    Return node
                End Function
            End Class

            Public Overrides Function VisitConditionalAccessReceiverPlaceholder(node As BoundConditionalAccessReceiverPlaceholder) As BoundNode
                If m_ConditionalAccessReceiverPlaceholderReplacementInfo Is Nothing OrElse m_ConditionalAccessReceiverPlaceholderReplacementInfo.PlaceholderId <> node.PlaceholderId Then
                    Throw ExceptionUtilities.Unreachable
                End If

                Return MyBase.VisitConditionalAccessReceiverPlaceholder(node)
            End Function

            Public Overrides Function VisitArrayCreation(node As BoundArrayCreation) As BoundNode
                Debug.Assert(node.ArrayLiteralOpt Is Nothing)

                Dim bounds As ImmutableArray(Of BoundExpression) = Me.VisitList(node.Bounds)
                Dim rewrittenInitializer As BoundArrayInitialization = DirectCast(Me.Visit(node.InitializerOpt), BoundArrayInitialization)
                Dim rewrittenType As TypeSymbol = Me.VisitType(node.Type)

                Dim boundsRequiresSpill As Boolean = NeedsSpill(bounds)
                Dim initRequiresSpill As Boolean = ArrayInitializerNeedsSpill(rewrittenInitializer)

                If Not boundsRequiresSpill AndAlso Not initRequiresSpill Then
                    Debug.Assert(rewrittenInitializer Is Nothing OrElse rewrittenInitializer.Kind = BoundKind.ArrayInitialization)
                    Return node.Update(node.IsParamArrayArgument,
                                       bounds,
                                       DirectCast(rewrittenInitializer, BoundArrayInitialization),
                                       Nothing,
                                       Nothing,
                                       rewrittenType)
                End If

                Dim builder As New SpillBuilder()
                bounds = SpillExpressionList(builder, bounds, firstArgumentIsAReceiverOfAMethodCall:=False)

                If rewrittenInitializer IsNot Nothing Then
                    rewrittenInitializer = rewrittenInitializer.Update(
                                                SpillExpressionList(builder, rewrittenInitializer.Initializers, firstArgumentIsAReceiverOfAMethodCall:=False),
                                                rewrittenInitializer.Type)
                End If

                Return builder.BuildSequenceAndFree(Me.F,
                                                    node.Update(node.IsParamArrayArgument,
                                                                bounds,
                                                                DirectCast(rewrittenInitializer, BoundArrayInitialization),
                                                                Nothing,
                                                                Nothing,
                                                                rewrittenType))


            End Function

            Private Function VisitArrayInitializationParts(node As BoundArrayInitialization) As BoundExpression
                Debug.Assert(node IsNot Nothing)

                Dim parts As ImmutableArray(Of BoundExpression) = node.Initializers
                Dim partCount As Integer = parts.Length

                Dim rewrittenParts(partCount - 1) As BoundExpression

                For index = 0 To partCount - 1
                    Dim part As BoundExpression = parts(index)
                    rewrittenParts(index) = If(part.Kind = BoundKind.ArrayInitialization,
                                               VisitArrayInitializationParts(
                                                   DirectCast(part, BoundArrayInitialization)),
                                               VisitExpression(part))
                Next

                Dim rewrittenType As TypeSymbol = VisitType(node.Type)
                Return node.Update(rewrittenParts.AsImmutableOrNull, rewrittenType)
            End Function

            Public Overrides Function VisitArrayInitialization(node As BoundArrayInitialization) As BoundNode
                If node Is Nothing Then
                    Return Nothing
                End If

                Return VisitArrayInitializationParts(DirectCast(node, BoundArrayInitialization))
            End Function

            Public Overrides Function VisitArrayAccess(node As BoundArrayAccess) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitArrayAccess(node), BoundArrayAccess)
                Dim expression As BoundExpression = rewritten.Expression
                Dim indices As ImmutableArray(Of BoundExpression) = rewritten.Indices

                If Not NeedsSpill(expression) AndAlso Not NeedsSpill(indices) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()
                Dim result = SpillExpressionsWithReceiver(expression, isReceiverOfAMethodCall:=False, expressions:=indices, spillBuilder:=builder)
                Return builder.BuildSequenceAndFree(Me.F,
                                                    rewritten.Update(result.ReceiverOpt,
                                                                     result.Arguments,
                                                                     rewritten.IsLValue,
                                                                     rewritten.Type))
            End Function

            Public Overrides Function VisitArrayLength(node As BoundArrayLength) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitArrayLength(node), BoundArrayLength)
                Dim expression As BoundExpression = rewritten.Expression

                If Not NeedsSpill(expression) Then
                    Return rewritten
                End If

                Debug.Assert(expression.Kind = BoundKind.SpillSequence)
                Dim spill = DirectCast(expression, BoundSpillSequence)
                Return SpillSequenceWithNewValue(spill,
                                                 rewritten.Update(spill.ValueOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitUnaryOperator(node As BoundUnaryOperator) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitUnaryOperator(node), BoundUnaryOperator)
                Dim operand As BoundExpression = rewritten.Operand

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spill = DirectCast(operand, BoundSpillSequence)
                Return SpillSequenceWithNewValue(spill,
                                                 rewritten.Update(rewritten.OperatorKind,
                                                                  spill.ValueOpt,
                                                                  rewritten.Checked,
                                                                  rewritten.ConstantValueOpt,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitBinaryConditionalExpression(node As BoundBinaryConditionalExpression) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitBinaryConditionalExpression(node), BoundBinaryConditionalExpression)
                Debug.Assert(rewritten.ConvertedTestExpression Is Nothing)
                Debug.Assert(rewritten.TestExpressionPlaceholder Is Nothing)
                Dim testExpression As BoundExpression = rewritten.TestExpression
                Dim elseExpression As BoundExpression = rewritten.ElseExpression

                If Not NeedsSpill(testExpression) AndAlso Not NeedsSpill(elseExpression) Then
                    Return rewritten
                End If

                Dim builder As New SpillBuilder()

                Dim tempLocal As LocalSymbol = Me.F.SynthesizedLocal(rewritten.Type)
                builder.AddLocal(tempLocal)

                builder.AddStatement(MakeAssignmentStatement(testExpression, tempLocal, builder))

                builder.AddStatement(
                    Me.F.If(
                        condition:=Me.F.ReferenceIsNothing(Me.F.Local(tempLocal, False)),
                        thenClause:=MakeAssignmentStatement(elseExpression, tempLocal, builder)))

                Return builder.BuildSequenceAndFree(Me.F,
                                                    Me.F.Local(tempLocal, False))
            End Function

            Public Overrides Function VisitTypeOf(node As BoundTypeOf) As BoundNode
                Dim rewritten = DirectCast(MyBase.VisitTypeOf(node), BoundTypeOf)
                Dim operand As BoundExpression = rewritten.Operand

                If Not NeedsSpill(operand) Then
                    Return rewritten
                End If

                Debug.Assert(operand.Kind = BoundKind.SpillSequence)
                Dim spill = DirectCast(operand, BoundSpillSequence)
                Return SpillSequenceWithNewValue(spill,
                                                 rewritten.Update(spill.ValueOpt,
                                                                  rewritten.IsTypeOfIsNotExpression,
                                                                  rewritten.TargetType,
                                                                  rewritten.Type))
            End Function

            Public Overrides Function VisitSequencePoint(node As BoundSequencePoint) As BoundNode
                Dim _storedSyntax As VisualBasicSyntaxNode = Me._enclosingSequencePointSyntax
                Me._enclosingSequencePointSyntax = node.Syntax
                Dim rewritten = MyBase.VisitSequencePoint(node)
                Me._enclosingSequencePointSyntax = _storedSyntax
                Return rewritten
            End Function

            Public Overrides Function VisitSequencePointExpression(node As BoundSequencePointExpression) As BoundNode
                Dim _storedSyntax As VisualBasicSyntaxNode = Me._enclosingSequencePointSyntax
                Me._enclosingSequencePointSyntax = node.Syntax
                Dim rewritten = DirectCast(MyBase.VisitSequencePointExpression(node), BoundSequencePointExpression)
                Me._enclosingSequencePointSyntax = _storedSyntax

                Dim expression As BoundExpression = rewritten.Expression

                If Not NeedsSpill(expression) Then
                    Return rewritten
                End If

                Debug.Assert(expression.Kind = BoundKind.SpillSequence)
                Dim spill = DirectCast(expression, BoundSpillSequence)
                Return SpillSequenceWithNewValue(spill,
                                                 rewritten.Update(spill.ValueOpt,
                                                                  rewritten.Type))
            End Function

        End Class
    End Class

End Namespace
