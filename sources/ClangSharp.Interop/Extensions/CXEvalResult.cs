// Copyright (c) .NET Foundation and Contributors. All Rights Reserved. Licensed under the MIT License (MIT). See License.md in the repository root for more information.

using System;
using static ClangSharp.Interop.CXEvalResultKind;

namespace ClangSharp.Interop;

public unsafe partial struct CXEvalResult : IDisposable, IEquatable<CXEvalResult>
{
    public CXEvalResult(IntPtr handle)
    {
        Handle = handle;
    }

    public double AsDouble => (Kind == CXEval_Float) ? clang.EvalResult_getAsDouble(this) : 0;

    public int AsInt => (Kind == CXEval_Int) ? clang.EvalResult_getAsInt(this) : 0;

    public long AsLongLong => (Kind == CXEval_Int) ? clang.EvalResult_getAsLongLong(this) : 0;

    public string AsStr
    {
        get
        {
            var pStr = Kind == CXEval_StrLiteral ? clang.EvalResult_getAsStr(this) : null;

            if (pStr is null)
            {
                return string.Empty;
            }

            return SpanExtensions.AsString(pStr);
        }
    }

    public ulong AsUnsigned => (Kind == CXEval_Int) ? clang.EvalResult_getAsUnsigned(this) : 0;

    public IntPtr Handle { get; set; }

    public bool IsUnsignedInt => (Kind == CXEval_Int) && (clang.EvalResult_isUnsignedInt(this) != 0);

    public CXEvalResultKind Kind => clang.EvalResult_getKind(this);

    public static explicit operator CXEvalResult(void* value) => new CXEvalResult((IntPtr)value);

    public static implicit operator void*(CXEvalResult value) => (void*)value.Handle;

    public static bool operator ==(CXEvalResult left, CXEvalResult right) => left.Handle == right.Handle;

    public static bool operator !=(CXEvalResult left, CXEvalResult right) => left.Handle != right.Handle;

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            clang.EvalResult_dispose(this);
            Handle = IntPtr.Zero;
        }
    }

    public override bool Equals(object? obj) => (obj is CXEvalResult other) && Equals(other);

    public bool Equals(CXEvalResult other) => this == other;

    public override int GetHashCode() => Handle.GetHashCode();
}
