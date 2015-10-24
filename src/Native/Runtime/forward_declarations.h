//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

// This file may be included by header files to forward declare common
// public types. The intent here is that .CPP files should need to
// include fewer header files.

#define FWD_DECL(x)             \
    class x;                    \
    typedef DPTR(x) PTR_##x;

// rtu
FWD_DECL(AllocHeap)
FWD_DECL(CObjectHeader)
FWD_DECL(CLREventStatic)
FWD_DECL(CrstHolder)
FWD_DECL(CrstStatic)
FWD_DECL(EEMethodInfo)
FWD_DECL(EECodeManager)
FWD_DECL(EEThreadId)
FWD_DECL(MethodInfo)
FWD_DECL(Module)
FWD_DECL(Object)
FWD_DECL(OBJECTHANDLEHolder)
FWD_DECL(PageEntry)
FWD_DECL(PAL_EnterHolder)
FWD_DECL(PAL_LeaveHolder)
FWD_DECL(SpinLock)
FWD_DECL(RCOBJECTHANDLEHolder)
FWD_DECL(RedhawkGCInterface)
FWD_DECL(RtuObjectRef)
FWD_DECL(RuntimeInstance)
FWD_DECL(SectionMethodList)
FWD_DECL(StackFrameIterator)
FWD_DECL(SyncClean)
FWD_DECL(SyncState)
FWD_DECL(Thread)
FWD_DECL(ThreadStore)
FWD_DECL(VirtualCallStubManager)

#ifdef FEATURE_RWX_MEMORY
namespace rh { 
    namespace util {
        FWD_DECL(MemRange)
        FWD_DECL(MemAccessMgr)
        FWD_DECL(WriteAccessHolder)
    }
}
#endif // FEATURE_RWX_MEMORY

// inc
FWD_DECL(EEInterfaceInfo)
FWD_DECL(EEType)

