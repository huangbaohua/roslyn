﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// A class that represents access to a source text and its version from a storage location.
    /// </summary>
    public abstract class TextLoader
    {
        public abstract Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken);

        /// <summary>
        /// Creates a new TextLoader from an already existing source text and version.
        /// </summary>
        public static TextLoader From(TextAndVersion textAndVersion)
        {
            if (textAndVersion == null)
            {
                throw new ArgumentNullException("textAndVersion");
            }

            return new TextDocumentLoader(textAndVersion);
        }

        /// <summary>
        /// Creates a TextLoader from a SourceTextContainer and version. 
        /// 
        /// The text obtained from the loader will be the current text of the container at the time
        /// the loader is accessed.
        /// </summary>
        public static TextLoader From(SourceTextContainer container, VersionStamp version, string filePath = null)
        {
            if (container == null)
            {
                throw new ArgumentNullException("container");
            }

            return new TextContainerLoader(container, version, filePath);
        }

        private class TextDocumentLoader : TextLoader
        {
            private readonly TextAndVersion textAndVersion;

            internal TextDocumentLoader(TextAndVersion textAndVersion)
            {
                this.textAndVersion = textAndVersion;
            }

            public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
            {
                return Task.FromResult(this.textAndVersion);
            }
        }

        private class TextContainerLoader : TextLoader
        {
            private readonly SourceTextContainer container;
            private readonly VersionStamp version;
            private readonly string filePath;

            internal TextContainerLoader(SourceTextContainer container, VersionStamp version, string filePath)
            {
                this.container = container;
                this.version = version;
                this.filePath = filePath;
            }

            public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
            {
                return Task.FromResult(TextAndVersion.Create(this.container.CurrentText, this.version, this.filePath));
            }
        }
    }
}