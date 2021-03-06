﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Composition
Imports Microsoft.CodeAnalysis.Host
Imports Microsoft.CodeAnalysis.Host.Mef
Imports Microsoft.CodeAnalysis.Rename

Namespace Microsoft.CodeAnalysis.VisualBasic.Rename
    <ExportLanguageServiceFactory(GetType(IRenameRewriterLanguageService), LanguageNames.VisualBasic), [Shared]>
    Friend Class VisualBasicRenameRewriterLanguageServiceFactory
        Implements ILanguageServiceFactory

        Public Function CreateLanguageService(provider As HostLanguageServices) As ILanguageService Implements ILanguageServiceFactory.CreateLanguageService
            Return New VisualBasicRenameRewriterLanguageService(provider)
        End Function
    End Class
End Namespace
