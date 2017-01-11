// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal partial class Interop
{
    internal partial class mincore
    {
        [MethodImpl(MethodImplOptions.InternalCall)]
        [RuntimeImport(Interop.Libraries.ErrorHandling, "SetLastError")]
        internal extern static void SetLastError(uint dwErrCode);
    }
}
