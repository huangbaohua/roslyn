﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Roslyn.Utilities;
using System.Globalization;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// A diagnostic (such as a compiler error or a warning), along with the location where it occurred.
    /// </summary>
    [DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
    internal class DiagnosticWithInfo : Diagnostic
    {
        private readonly DiagnosticInfo info;
        private readonly Location location;

        internal DiagnosticWithInfo(DiagnosticInfo info, Location location)
        {
            Debug.Assert(info != null);
            Debug.Assert(location != null);
            this.info = info;
            this.location = location;
        }

        public override Location Location
        {
            get { return this.location; }
        }

        public override IReadOnlyList<Location> AdditionalLocations
        {
            get { return this.Info.AdditionalLocations; }
        }

        internal override IReadOnlyList<string> CustomTags
        {
            get
            {
                return this.Info.CustomTags;
            }
        }

        public override DiagnosticDescriptor Descriptor
        {
            get
            {
                return this.Info.Descriptor;
            }
        }

        public override string Id
        {
            get { return this.Info.MessageIdentifier; }
        }

        internal override string Category
        {
            get { return this.Info.Category; }
        }


        internal sealed override int Code
        {
            get { return this.Info.Code; }
        }

        public sealed override DiagnosticSeverity Severity
        {
            get { return this.Info.Severity; }
        }

        public sealed override DiagnosticSeverity DefaultSeverity
        {
            get { return this.Info.DefaultSeverity; }
        }

        internal sealed override bool IsEnabledByDefault
        {
            // All compiler errors and warnings are enabled by default.
            get { return true; }
        }

        public sealed override int WarningLevel
        {
            get { return this.Info.WarningLevel; }
        }

        public override string GetMessage(IFormatProvider formatProvider = null)
        {
            return this.Info.GetMessage(formatProvider);
        }

        internal override IReadOnlyList<object> Arguments
        {
            get { return this.Info.Arguments; }
        }

        /// <summary>
        /// Get the information about the diagnostic: the code, severity, message, etc.
        /// </summary>
        public DiagnosticInfo Info
        {
            get
            {
                if (this.info.Severity == InternalDiagnosticSeverity.Unknown)
                {
                    return this.info.GetResolvedInfo();
                }

                return this.info;
            }
        }

        /// <summary>
        /// True if the DiagnosticInfo for this diagnostic requires (or required - this property
        /// is immutable) resolution.
        /// </summary>
        internal bool HasLazyInfo
        {
            get
            {
                return this.info.Severity == InternalDiagnosticSeverity.Unknown ||
                    this.info.Severity == InternalDiagnosticSeverity.Void;
            }
        }

        public override int GetHashCode()
        {
            return Hash.Combine(this.Location.GetHashCode(), this.Info.GetHashCode());
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Diagnostic);
        }

        public override bool Equals(Diagnostic obj)
        {
            if (this == obj)
            {
                return true;
            }

            var other = obj as DiagnosticWithInfo;

            if (other == null || this.GetType() != other.GetType())
            {
                return false;
            }

            return
                this.Location.Equals(other.location) &&
                this.Info.Equals(other.Info) &&
                this.AdditionalLocations.SequenceEqual(other.AdditionalLocations);
        }

        private string GetDebuggerDisplay()
        {
            switch (info.Severity)
            {
                case InternalDiagnosticSeverity.Unknown:
                    // If we called ToString before the diagnostic was resolved,
                    // we would risk infinite recursion (e.g. if we were still computing
                    // member lists).
                    return "Unresolved diagnostic at " + this.Location;

                case InternalDiagnosticSeverity.Void:
                    // If we called ToString on a void diagnostic, the MessageProvider
                    // would complain about the code.
                    return "Void diagnostic at " + this.Location;

                default:
                    return ToString();
            }
        }

        internal override Diagnostic WithLocation(Location location)
        {
            if (location == null)
            {
                throw new ArgumentNullException("location");
            }

            if (location != this.location)
            {
                return new DiagnosticWithInfo(this.info, location);
            }

            return this;
        }

        internal override Diagnostic WithSeverity(DiagnosticSeverity severity)
        {
            if (this.Severity != severity)
            {
                return new DiagnosticWithInfo(this.Info.GetInstanceWithSeverity(severity), this.location);
            }

            return this;
        }

        internal sealed override bool IsNotConfigurable()
        {
            return this.Info.IsNotConfigurable();
        }
    }
}
