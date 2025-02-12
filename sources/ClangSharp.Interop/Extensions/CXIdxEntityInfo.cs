// Copyright (c) .NET Foundation and Contributors. All Rights Reserved. Licensed under the MIT License (MIT). See License.md in the repository root for more information.

namespace ClangSharp.Interop;

public unsafe partial struct CXIdxEntityInfo
{
    public CXIdxClientEntity ClientEntity
    {
        get
        {
            fixed (CXIdxEntityInfo* pThis = &this)
            {
                return (CXIdxClientEntity)clang.index_getClientEntity(pThis);
            }
        }

        set
        {
            fixed (CXIdxEntityInfo* pThis = &this)
            {
                clang.index_setClientEntity(pThis, value);
            }
        }
    }

    public bool IsObjCContainer => clang.index_isEntityObjCContainerKind(kind) != 0;
}
