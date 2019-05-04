﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Shared
{
    /// <summary>
    ///     Literals shared by Proxy Server
    /// </summary>
    internal class ProxyConstants
    {
        internal static readonly char DotSplit = '.';

        internal static readonly char[] SpaceSplit = { ' ' };
        internal static readonly char[] ColonSplit = { ':' };
        internal static readonly char[] SemiColonSplit = { ';' };
        internal static readonly char[] EqualSplit = { '=' };

        internal static readonly string NewLine = "\r\n";
        internal static readonly byte[] NewLineBytes = { (byte)'\r', (byte)'\n' };

        internal static readonly Regex UriSchemeRegex =
            new Regex("^[a-z]*://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static readonly HashSet<string> ProxySupportedCompressions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                KnownHeaders.ContentEncodingGzip,
                KnownHeaders.ContentEncodingDeflate,
                KnownHeaders.ContentEncodingBrotli
            };

        internal static readonly Regex CNRemoverRegex =
            new Regex(@"^CN\s*=\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
