﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Globalization
Imports System.Runtime.InteropServices
Imports Microsoft.CodeAnalysis.Collections

Namespace Microsoft.CodeAnalysis.VisualBasic.Symbols

    ''' <summary>
    ''' Helper class to generate synthesized names.
    ''' </summary>
    Friend NotInheritable Class GeneratedNames

        ''' <summary>
        ''' Generates the name of an operator's function local based on the operator name.
        ''' </summary>
        Public Shared Function MakeOperatorLocalName(name As String) As String
            Debug.Assert(name.StartsWith("op_"))
            Return String.Format(StringConstants.OperatorLocalName, name)
        End Function

        ''' <summary>
        ''' Generates the name of a state machine's type
        ''' </summary>
        Public Shared Function MakeStateMachineTypeName(index As Integer, topMethodMetadataName As String) As String
            topMethodMetadataName = EnsureNoDotsInTypeName(topMethodMetadataName)
            Return String.Format(StringConstants.StateMachineTypeNameMask, index, topMethodMetadataName)
        End Function

        Public Shared Function TryParseStateMachineTypeName(stateMachineTypeName As String, <Out> ByRef index As Integer, <Out> ByRef methodName As String) As Boolean
            If Not stateMachineTypeName.StartsWith(StringConstants.StateMachineTypeNamePrefix, StringComparison.Ordinal) Then
                Return False
            End If

            Dim prefixLength As Integer = StringConstants.StateMachineTypeNamePrefix.Length
            Dim separatorPos = stateMachineTypeName.IndexOf("_"c, prefixLength)
            If separatorPos < 0 OrElse separatorPos = stateMachineTypeName.Length - 1 Then
                Return False
            End If

            If Not Integer.TryParse(stateMachineTypeName.Substring(prefixLength, separatorPos - prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, index) Then
                Return False
            End If

            methodName = stateMachineTypeName.Substring(separatorPos + 1)
            Return True
        End Function

        Public Shared Function EnsureNoDotsInTypeName(Name As String) As String
            ' CLR generally allows names with dots, however some APIs like IMetaDataImport
            ' can only return full type names combined with namespaces. 
            ' see: http://msdn.microsoft.com/en-us/library/ms230143.aspx (IMetaDataImport::GetTypeDefProps)
            ' When working with such APIs, names with dots become ambiguous since metadata 
            ' consumer cannot figure where namespace ends and actual type name starts.
            ' Therefore it is a good practice to avoid type names with dots.
            If (Name.IndexOf("."c) >= 0) Then
                Name = Name.Replace("."c, "_"c)
            End If

            Return Name
        End Function

        ''' <summary>
        ''' Generates the name of a state machine 'builder' field 
        ''' </summary>
        Public Shared Function MakeStateMachineBuilderFieldName() As String
            Return StringConstants.StateMachineBuilderFieldName
        End Function

        ''' <summary>
        ''' Generates the name of a state machine 'state' field 
        ''' </summary>
        Public Shared Function MakeStateMachineStateFieldName() As String
            Return StringConstants.StateMachineStateFieldName
        End Function

        ''' <summary>
        ''' Generates the name of a state machine's 'awaiter_xyz' field 
        ''' </summary>
        Public Shared Function MakeStateMachineAwaiterFieldName(index As Integer) As String
            Return StringConstants.StateMachineAwaiterFieldPrefix & index
        End Function

        ''' <summary>
        ''' Generates the name of a state machine's parameter name
        ''' </summary>
        Public Shared Function MakeStateMachineParameterName(paramName As String) As String
            Return StringConstants.HoistedUserVariablePrefix & paramName
        End Function

        ''' <summary>
        ''' Generates the name of a state machine's parameter name
        ''' </summary>
        Public Shared Function MakeIteratorParameterProxyName(paramName As String) As String
            Return StringConstants.IteratorParameterProxyPrefix & paramName
        End Function

        ''' <summary>
        ''' Generates the name of a static lambda display class instance cache
        ''' </summary>
        ''' <returns></returns>
        Public Shared Function MakeCachedFrameInstanceName() As String
            Return StringConstants.CachedFrameInstanceName
        End Function

        ''' <summary>
        ''' Generates the name of a field that backs Current property
        ''' </summary>
        Public Shared Function MakeIteratorCurrentFieldName() As String
            Return StringConstants.IteratorCurrentFieldName
        End Function

        ''' <summary>
        ''' Generates the name of a field where initial thread ID is stored
        ''' </summary>
        Public Shared Function MakeIteratorInitialThreadIdName() As String
            Return StringConstants.IteratorInitialThreadIdName
        End Function

        ''' <summary>
        ''' Try to parse the local (or parameter) name and return <paramref name="variableName"/> if successful.
        ''' </summary>
        Public Shared Function TryParseHoistedUserVariableName(proxyName As String, <Out()> ByRef variableName As String) As Boolean
            variableName = Nothing

            Dim prefixLen As Integer = StringConstants.HoistedUserVariablePrefix.Length
            If proxyName.Length <= prefixLen Then
                Return False
            End If

            ' All names should start with "$VB$Local_"
            If Not proxyName.StartsWith(StringConstants.HoistedUserVariablePrefix) Then
                Return False
            End If

            variableName = proxyName.Substring(prefixLen)
            Return True
        End Function

        ''' <summary>
        ''' Try to parse the local name and return <paramref name="variableName"/> and <paramref name="index"/> if successful.
        ''' </summary>
        Public Shared Function TryParseStateMachineHoistedUserVariableName(proxyName As String, <Out()> ByRef variableName As String, <Out()> ByRef index As Integer) As Boolean
            variableName = Nothing
            index = 0

            ' All names should start with "$VB$ResumableLocal_"
            If Not proxyName.StartsWith(StringConstants.StateMachineHoistedUserVariablePrefix) Then
                Return False
            End If

            Dim prefixLen As Integer = StringConstants.StateMachineHoistedUserVariablePrefix.Length
            Dim separator As Integer = proxyName.LastIndexOf("$"c)
            If separator <= prefixLen Then
                Return False
            End If

            variableName = proxyName.Substring(prefixLen, separator - prefixLen)
            Return Integer.TryParse(proxyName.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, index)
        End Function

        ''' <summary>
        ''' Generates the name of a state machine field name for captured me reference
        ''' </summary>
        Public Shared Function MakeStateMachineCapturedMeName() As String
            Return StringConstants.HoistedMeName
        End Function

        ''' <summary>
        ''' Generates the name of a state machine field name for captured me reference of lambda closure
        ''' </summary>
        Public Shared Function MakeStateMachineCapturedClosureMeName(closureName As String) As String
            Return StringConstants.HoistedSpecialVariablePrefix & closureName
        End Function

        Friend Const AnonymousTypeOrDelegateCommonPrefix = "VB$Anonymous"
        Friend Const AnonymousTypeTemplateNamePrefix = AnonymousTypeOrDelegateCommonPrefix & "Type_"
        Friend Const AnonymousDelegateTemplateNamePrefix = AnonymousTypeOrDelegateCommonPrefix & "Delegate_"

        Friend Shared Function MakeAnonymousTypeTemplateName(prefix As String, index As Integer, submissionSlotIndex As Integer, moduleId As String) As String
            Return If(submissionSlotIndex >= 0,
                           String.Format("{0}{1}_{2}{3}", prefix, submissionSlotIndex, index, moduleId),
                           String.Format("{0}{1}{2}", prefix, index, moduleId))
        End Function

        Friend Shared Function TryParseAnonymousTypeTemplateName(prefix As String, name As String, <Out()> ByRef index As Integer) As Boolean
            ' No callers require anonymous types from net modules,
            ' so names with module id are ignored.
            If name.StartsWith(prefix, StringComparison.Ordinal) AndAlso
                Integer.TryParse(name.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, index) Then
                Return True
            End If
            index = -1
            Return False
        End Function

        Friend Shared Function MakeSynthesizedLocalName(kind As SynthesizedLocalKind, ByRef uniqueId As Integer) As String
            Debug.Assert(kind.IsLongLived())

            ' The following variables have to be named, EE depends on the name format.
            Dim name As String
            Select Case kind
                Case SynthesizedLocalKind.LambdaDisplayClass
                    name = MakeLambdaDisplayClassStorageName(uniqueId)
                    uniqueId += 1

                Case SynthesizedLocalKind.With
                    ' Dev12 didn't name the local. We do so that we can do better job in EE evaluating With statements.
                    name = StringConstants.HoistedWithLocalPrefix & uniqueId
                    uniqueId += 1

                Case Else
                    name = Nothing
            End Select

            Return name
        End Function

        Friend Shared Function MakeLambdaMethodName(index As Integer) As String
            Return StringConstants.LAMBDA_PREFIX & index
        End Function

        Friend Shared Function MakeLambdaDisplayClassName(index As Integer) As String
            Return StringConstants.DisplayClassPrefix & index
        End Function

        Friend Shared Function MakeLambdaDisplayClassStorageName(uniqueId As Integer) As String
            Return StringConstants.ClosureVariablePrefix & uniqueId
        End Function

        Friend Shared Function MakeSignatureString(signature As Byte()) As String
            Dim builder = PooledStringBuilder.GetInstance()
            For Each b In signature
                ' Note the format of each byte is not fixed width, so the resulting string may be
                ' ambiguous. And since this method Is used to generate field names for static
                ' locals, the same field name may be generated for two locals with the same
                ' local name in overloaded methods. The native compiler has the same behavior.
                ' Using a fixed width format {0:X2} would solve this but since the EE relies on
                ' the format for recognizing static locals, that would be a breaking change.
                builder.Builder.AppendFormat("{0:X}", b)
            Next
            Return builder.ToStringAndFree()
        End Function

        Friend Shared Function MakeStaticLocalFieldName(
            methodName As String,
            methodSignature As String,
            localName As String) As String

            Return String.Format(StringConstants.StaticLocalFieldNameMask, methodName, methodSignature, localName)
        End Function

        Friend Shared Function TryParseStaticLocalFieldName(
            fieldName As String,
            <Out> ByRef methodName As String,
            <Out> ByRef methodSignature As String,
            <Out> ByRef localName As String) As Boolean

            If fieldName.StartsWith(StringConstants.StaticLocalFieldNamePrefix, StringComparison.Ordinal) Then
                Dim parts = fieldName.Split("$"c)
                If parts.Length = 5 Then
                    methodName = parts(2)
                    methodSignature = parts(3)
                    localName = parts(4)
                    Return True
                End If
            End If

            methodName = Nothing
            methodSignature = Nothing
            localName = Nothing
            Return False
        End Function

        ' Extracts the slot index from a name of a field that stores hoisted variables Or awaiters.
        ' Such a name ends with "$prefix{slot index}". 
        ' Returned slot index Is >= 0.
        Friend Shared Function TryParseSlotIndex(prefix As String, fieldName As String, <Out> ByRef slotIndex As Integer) As Boolean
            If fieldName.StartsWith(prefix, StringComparison.Ordinal) AndAlso
                Integer.TryParse(fieldName.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, slotIndex) Then
                Return True
            End If
            slotIndex = -1
            Return False
        End Function

    End Class

End Namespace