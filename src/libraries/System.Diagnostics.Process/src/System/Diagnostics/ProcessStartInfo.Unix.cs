// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security;

namespace System.Diagnostics
{
    // We know of no way to achieve this on Unix, particularly providing the password
    // without a prompt. If we find a way, we should implement it. It may make more sense to provide
    // similar functionality through an API specific to Unix.
    public sealed partial class ProcessStartInfo
    {
        [SupportedOSPlatform("windows")]
        public string? PasswordInClearText
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(PasswordInClearText))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(PasswordInClearText))); }
        }

        [SupportedOSPlatform("windows")]
        [AllowNull]
        public string Domain
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(Domain))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(Domain))); }
        }

        [SupportedOSPlatform("windows")]
        public bool LoadUserProfile
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(LoadUserProfile))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(LoadUserProfile))); }
        }

        [SupportedOSPlatform("windows")]
        public bool UseCredentialsForNetworkingOnly
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(UseCredentialsForNetworkingOnly))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(UseCredentialsForNetworkingOnly))); }
        }

        public bool UseShellExecute { get; set; }

        public string[] Verbs => Array.Empty<string>();

        [CLSCompliant(false)]
        [SupportedOSPlatform("windows")]
        public SecureString? Password
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(Password))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(Password))); }
        }

        [SupportedOSPlatform("windows")]
        public bool CreateNewProcessGroup
        {
            get { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(CreateNewProcessGroup))); }
            set { throw new PlatformNotSupportedException(SR.Format(SR.ProcessStartSingleFeatureNotSupported, nameof(CreateNewProcessGroup))); }
        }
    }
}
