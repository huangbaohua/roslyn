﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;

namespace Microsoft.CodeAnalysis.Test.Utilities
{
    public class TempDirectory
    {
        private readonly string path;
        private readonly TempRoot root;

        protected TempDirectory(TempRoot root) 
            : this(CreateUniqueDirectory(TempRoot.Root), root)
        {
        }

        private TempDirectory(string path, TempRoot root)
        {
            Debug.Assert(path != null);
            Debug.Assert(root != null);

            this.path = path;
            this.root = root;
        }

        private static string CreateUniqueDirectory(string basePath)
        {
            while (true)
            {
                string dir = System.IO.Path.Combine(basePath, Guid.NewGuid().ToString());
                try
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (IOException)
                {
                    // retry
                }
            }
        }

        public string Path
        {
            get { return path; }
        }

        /// <summary>
        /// Creates a file in this directory.
        /// </summary>
        /// <param name="name">File name.</param>
        public TempFile CreateFile(string name)
        {
            string filePath = System.IO.Path.Combine(path, name);
            TempRoot.CreateStream(filePath);
            return root.AddFile(new DisposableFile(filePath));
        }

        /// <summary>
        /// Creates a subdirectory in this directory.
        /// </summary>
        /// <param name="name">Directory name or unrooted directory path.</param>
        public TempDirectory CreateDirectory(string name)
        {
            string dirPath = System.IO.Path.Combine(path, name);
            Directory.CreateDirectory(dirPath);
            return new TempDirectory(dirPath, root);
        }

        public override string ToString()
        {
            return path;
        }
    }
}
