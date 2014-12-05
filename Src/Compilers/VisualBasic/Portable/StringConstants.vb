﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Namespace Microsoft.CodeAnalysis.VisualBasic

    Friend NotInheritable Class StringConstants

        Private Sub New()
        End Sub

        ' non localizable strings
        Friend Const AnonymousTypeName As String = "<anonymous type>"
        Friend Const AnonymousMethodName As String = "<anonymous method>"
        Friend Const AsEnumerableMethod As String = "AsEnumerable"
        Friend Const AsQueryableMethod As String = "AsQueryable"
        Friend Const DistinctMethod As String = "Distinct"
        Friend Const CastMethod As String = "Cast"
        Friend Const DelegateConstructorInstanceParameterName As String = "TargetObject"
        Friend Const DelegateConstructorMethodParameterName As String = "TargetMethod"
        Friend Const DelegateMethodCallbackParameterName As String = "DelegateCallback"
        Friend Const DelegateMethodInstanceParameterName As String = "DelegateAsyncState"
        Friend Const DelegateMethodResultParameterName As String = "DelegateAsyncResult"
        Friend Const DelegateStubParameterName As String = "a{0}"
        Friend Const ElementAtMethod As String = "ElementAtOrDefault"
        Friend Const Group As String = "$VB$Group"
        Friend Const GroupByMethod As String = "GroupBy"
        Friend Const GroupJoinMethod As String = "GroupJoin"
        Friend Const It As String = "$VB$It"
        Friend Const It1 As String = "$VB$It1"
        Friend Const It2 As String = "$VB$It2"
        Friend Const ItAnonymous As String = "$VB$ItAnonymous"
        Friend Const JoinMethod As String = "Join"
        Friend Const Lambda As String = "Lambda"
        Friend Const NamedSymbolErrorName As String = "?"
        Friend Const OperatorLocalName As String = "VB${0}"
        Friend Const OrderByDescendingMethod As String = "OrderByDescending"
        Friend Const OrderByMethod As String = "OrderBy"
        Friend Const SelectManyMethod As String = "SelectMany"
        Friend Const SelectMethod As String = "Select"
        Friend Const SkipMethod As String = "Skip"
        Friend Const SkipWhileMethod As String = "SkipWhile"
        Friend Const TakeMethod As String = "Take"
        Friend Const TakeWhileMethod As String = "TakeWhile"
        Friend Const ThenByDescendingMethod As String = "ThenByDescending"
        Friend Const ThenByMethod As String = "ThenBy"
        Friend Const UnnamedNamespaceErrName As String = "<Default>"
        Friend Const WhereMethod As String = "Where"
        Friend Const LiftedMeName As String = "$VB$Me"
        Friend Const LiftedNonLocalPrefix As String = "$VB$NonLocal_"
        Friend Const LiftedLocalPrefix As String = "$VB$Local_"
        Friend Const ClosureClassPrefix As String = "_Closure$__"
        Friend Const ClosureVariablePrefix As String = "$VB$Closure_"
        Friend Const CachedFrameInstanceName As String = "$Inst"

        Friend Const OnErrorActiveHandler As String = "VB$ActiveHandler"
        Friend Const OnErrorResumeTarget As String = "VB$ResumeTarget"
        Friend Const OnErrorCurrentStatement As String = "VB$CurrentStatement"
        Friend Const OnErrorCurrentLine As String = "VB$CurrentLine"
        Friend Const StateMachineCachedState As String = "VB$cachedState"

        Friend Const SynthesizedLocalKindSelectCaseValue As String = "VB$SelectCaseValue"
        Friend Const SynthesizedLocalKindLock As String = "VB$Lock"
        Friend Const SynthesizedLocalKindUsing As String = "VB$Using"
        Friend Const SynthesizedLocalKindForEachEnumerator As String = "VB$ForEachEnumerator"
        Friend Const SynthesizedLocalKindForEachArray As String = "VB$ForEachArray"
        Friend Const SynthesizedLocalKindForEachArrayIndex As String = "VB$ForEachArrayIndex"
        Friend Const SynthesizedLocalKindLockTaken As String = "VB$LockTaken"
        Friend Const SynthesizedLocalKindWith As String = "VB$With_"

        Friend Const ForLimit As String = "VB$ForLimit"
        Friend Const ForStep As String = "VB$ForStep"
        Friend Const ForLoopObject As String = "VB$LoopObject"
        Friend Const ForDirection As String = "VB$LoopDirection"

        Friend Const StateMachineTypeNamePrefix As String = "VB$StateMachine_"
        Friend Const StateMachineTypeNameMask As String = StateMachineTypeNamePrefix & "{0}_{1}"
        Friend Const StateMachineLocalNamePrefix As String = "$VB$ResumableLocal_"
        Friend Const StateMachineLocalNameMask As String = StateMachineLocalNamePrefix & "{1}${0}"
        Friend Const StateMachineExceptionLocalName As String = "$ex"
        Friend Const StateMachineReturnValueLocalName As String = "VB$returnTemp"

        ' Microsoft.VisualStudio.VIL.VisualStudioHost.AsyncReturnStackFrame depends on these names.
        Friend Const StateMachineBuilderFieldName As String = "$Builder"
        Friend Const StateMachineStateFieldName As String = "$State"

        Friend Const StateMachineAwaiterFieldName As String = "$awaiter_{0}"
        Friend Const StateMachineStackSpillNameMask As String = "VB$StackSpill_${0}"
        Friend Const StateMachineExpressionCaptureNameMask As String = "VB$ExpressionCapture_${0}"
        Friend Const StateMachineTypeParameterPrefix As String = "SM$"

        Friend Const IteratorCurrentFieldName As String = "$Current"
        Friend Const IteratorInitialThreadIdName As String = "$InitialThreadId"
        Friend Const IteratorParameterProxyName As String = "proto$"

        Friend Const StaticLocalFieldNamePrefix = "$STATIC$"
        Friend Const StaticLocalFieldNameMask = StaticLocalFieldNamePrefix & "{0}${1}${2}"

        Friend Const PropertyGetPrefix As String = "get_"
        Friend Const PropertySetPrefix As String = "set_"
        Friend Const WinMdPropertySetPrefix As String = "put_"

        Friend Const ValueParameterName As String = "Value"
        Friend Const WithEventsValueParameterName As String = "WithEventsValue"
        Friend Const AutoPropertyValueParameterName As String = "AutoPropertyValue"

        Friend Const DefaultXmlnsPrefix As String = ""
        Friend Const DefaultXmlNamespace As String = ""
        Friend Const XmlPrefix As String = "xml"
        Friend Const XmlNamespace As String = "http://www.w3.org/XML/1998/namespace"
        Friend Const XmlnsPrefix As String = "xmlns"
        Friend Const XmlnsNamespace As String = "http://www.w3.org/2000/xmlns/"

        Friend Const XmlAddMethodName As String = "Add"
        Friend Const XmlGetMethodName As String = "Get"
        Friend Const XmlElementsMethodName As String = "Elements"
        Friend Const XmlDescendantsMethodName As String = "Descendants"
        Friend Const XmlAttributeValueMethodName As String = "AttributeValue"
        Friend Const XmlCreateAttributeMethodName As String = "CreateAttribute"
        Friend Const XmlCreateNamespaceAttributeMethodName As String = "CreateNamespaceAttribute"
        Friend Const XmlRemoveNamespaceAttributesMethodName As String = "RemoveNamespaceAttributes"

        Friend Const ValueProperty As String = "Value"

        '// !! Do not change the following strings. Other teams (FxCop) use this string to identify lambda functions in its analysis
        '// If you have to change this string, please contact the VB language PM and consider the impact of that break.
        Friend Const LAMBDA_PREFIX As String = "_Lambda$__"
        Friend Const CLOSURE_GENERICPARAM_PREFIX As String = "$CLS"
        Friend Const CLOSURE_MYSTUB_PREFIX As String = "$VB$ClosureStub_"

    End Class

    Friend Module Constants
        Friend Const ATTACH_LISTENER_PREFIX As String = "add_"

        Friend Const REMOVE_LISTENER_PREFIX As String = "remove_"

        Friend Const FIRE_LISTENER_PREFIX As String = "raise_"

        Friend Const EVENT_DELEGATE_SUFFIX As String = "EventHandler"

        Friend Const EVENT_VARIABLE_SUFFIX As String = "Event"
    End Module
End Namespace
