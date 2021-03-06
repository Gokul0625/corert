﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Internal.TypeSystem;

using ILCompiler;
using ILCompiler.DependencyAnalysis;

using Debug = System.Diagnostics.Debug;
using DependencyList = ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<ILCompiler.DependencyAnalysis.NodeFactory>.DependencyList;

namespace Internal.IL
{
    // Implements an IL scanner that scans method bodies to be compiled by the code generation
    // backend before the actual compilation happens to gain insights into the code.
    partial class ILImporter
    {
        private readonly MethodIL _methodIL;
        private readonly ILScanner _compilation;
        private readonly ILScanNodeFactory _factory;

        // True if we're scanning a throwing method body because scanning the real body failed.
        private readonly bool _isFallbackBodyCompilation;

        private readonly MethodDesc _canonMethod;

        private readonly DependencyList _dependencies = new DependencyList();

        private readonly byte[] _ilBytes;
        
        private class BasicBlock
        {
            // Common fields
            public BasicBlock Next;

            public int StartOffset;
            public int EndOffset;

            public bool TryStart;
            public bool FilterStart;
            public bool HandlerStart;
        }

        private TypeDesc _constrained;

        private int _currentInstructionOffset;
        private int _previousInstructionOffset;

        private class ExceptionRegion
        {
            public ILExceptionRegion ILRegion;
        }
        private ExceptionRegion[] _exceptionRegions;

        public ILImporter(ILScanner compilation, MethodDesc method, MethodIL methodIL = null)
        {
            if (methodIL == null)
            {
                methodIL = compilation.GetMethodIL(method);
            }
            else
            {
                _isFallbackBodyCompilation = true;
            }

            // This is e.g. an "extern" method in C# without a DllImport or InternalCall.
            if (methodIL == null)
            {
                throw new TypeSystemException.InvalidProgramException(ExceptionStringID.InvalidProgramSpecific, method);
            }

            _compilation = compilation;
            _factory = (ILScanNodeFactory)compilation.NodeFactory;
            
            _ilBytes = methodIL.GetILBytes();

            // Get the runtime determined method IL so that this works right in shared code
            // and tokens in shared code resolve to runtime determined types.
            MethodIL uninstantiatiedMethodIL = methodIL.GetMethodILDefinition();
            if (methodIL != uninstantiatiedMethodIL)
            {
                MethodDesc sharedMethod = method.GetSharedRuntimeFormMethodTarget();
                _methodIL = new InstantiatedMethodIL(sharedMethod, uninstantiatiedMethodIL);
            }
            else
            {
                _methodIL = methodIL;
            }

            _canonMethod = method;

            var ilExceptionRegions = methodIL.GetExceptionRegions();
            _exceptionRegions = new ExceptionRegion[ilExceptionRegions.Length];
            for (int i = 0; i < ilExceptionRegions.Length; i++)
            {
                _exceptionRegions[i] = new ExceptionRegion() { ILRegion = ilExceptionRegions[i] };
            }
        }

        public DependencyList Import()
        {
            if (_canonMethod.Signature.IsStatic)
            {
                TypeDesc owningType = _canonMethod.OwningType;
                if (!_isFallbackBodyCompilation && _factory.TypeSystemContext.HasLazyStaticConstructor(owningType))
                {
                    // For beforefieldinit, we can wait for field access.
                    if (!((MetadataType)owningType).IsBeforeFieldInit)
                    {
                        MethodDesc method = _methodIL.OwningMethod;
                        if (method.OwningType.IsRuntimeDeterminedSubtype)
                        {
                            _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.GetNonGCStaticBase, method.OwningType), "Owning type cctor");
                        }
                        else
                        {
                            _dependencies.Add(_factory.ReadyToRunHelper(ReadyToRunHelperId.GetNonGCStaticBase, method.OwningType), "Owning type cctor");
                        }
                    }
                }
            }

            FindBasicBlocks();
            ImportBasicBlocks();

            return _dependencies;
        }

        private ISymbolNode GetGenericLookupHelper(ReadyToRunHelperId helperId, object helperArgument)
        {
            if (_canonMethod.RequiresInstMethodDescArg())
            {
                return _compilation.NodeFactory.ReadyToRunHelperFromDictionaryLookup(helperId, helperArgument, _canonMethod);
            }
            else
            {
                Debug.Assert(_canonMethod.RequiresInstArg() || _canonMethod.AcquiresInstMethodTableFromThis());
                return _compilation.NodeFactory.ReadyToRunHelperFromTypeLookup(helperId, helperArgument, _canonMethod.OwningType);
            }
        }

        private ISymbolNode GetHelperEntrypoint(ReadyToRunHelper helper)
        {
            string mangledName;
            MethodDesc methodDesc;
            JitHelper.GetEntryPoint(_compilation.TypeSystemContext, helper, out mangledName, out methodDesc);
            Debug.Assert(mangledName != null || methodDesc != null);

            ISymbolNode entryPoint;
            if (mangledName != null)
                entryPoint = _compilation.NodeFactory.ExternSymbol(mangledName);
            else
                entryPoint = _compilation.NodeFactory.MethodEntrypoint(methodDesc);

            return entryPoint;
        }

        private void MarkInstructionBoundary() { }
        private void EndImportingBasicBlock(BasicBlock basicBlock) { }
        private void EndImportingInstruction() { }

        private void StartImportingBasicBlock(BasicBlock basicBlock)
        {
            // Import all associated EH regions
            foreach (ExceptionRegion ehRegion in _exceptionRegions)
            {
                ILExceptionRegion region = ehRegion.ILRegion;
                if (region.TryOffset == basicBlock.StartOffset)
                {
                    MarkBasicBlock(_basicBlocks[region.HandlerOffset]);
                    if (region.Kind == ILExceptionRegionKind.Filter)
                        MarkBasicBlock(_basicBlocks[region.FilterOffset]);
                }
            }

            _currentInstructionOffset = -1;
            _previousInstructionOffset = -1;
        }

        private void StartImportingInstruction()
        {
            _previousInstructionOffset = _currentInstructionOffset;
            _currentInstructionOffset = _currentOffset;
        }

        private void ImportJmp(int token)
        {
            // TODO
        }

        private void ImportCasting(ILOpcode opcode, int token)
        {
            TypeDesc type = (TypeDesc)_methodIL.GetObject(token);

            // Nullable needs to be unwrapped
            if (type.IsNullable)
                type = type.Instantiation[0];

            if (type.IsRuntimeDeterminedSubtype)
            {
                _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, type), "IsInst/CastClass");
            }
            else
            {
                ReadyToRunHelperId helperId;
                if (opcode == ILOpcode.isinst)
                {
                    helperId = ReadyToRunHelperId.IsInstanceOf;
                }
                else
                {
                    Debug.Assert(opcode == ILOpcode.castclass);
                    helperId = ReadyToRunHelperId.CastClass;
                }

                _dependencies.Add(_factory.ReadyToRunHelper(helperId, type), "IsInst/CastClass");
            }
        }
        
        private void ImportCall(ILOpcode opcode, int token)
        {
            // Strip runtime determined characteristics off of the method (because that's how RyuJIT operates)
            var runtimeDeterminedMethod = (MethodDesc)_methodIL.GetObject(token);
            MethodDesc method = runtimeDeterminedMethod;
            if (runtimeDeterminedMethod.IsRuntimeDeterminedExactMethod)
                method = runtimeDeterminedMethod.GetCanonMethodTarget(CanonicalFormKind.Specific);

            if (method.IsRawPInvoke())
            {
                // Raw P/invokes don't have any dependencies.
                return;
            }

            string reason = null;
            switch (opcode)
            {
                case ILOpcode.newobj:
                    reason = "newobj"; break;
                case ILOpcode.call:
                    reason = "call"; break;
                case ILOpcode.callvirt:
                    reason = "callvirt"; break;
                case ILOpcode.ldftn:
                    reason = "ldftn"; break;
                case ILOpcode.ldvirtftn:
                    reason = "ldvirtftn"; break;
                default:
                    Debug.Assert(false); break;
            }

            // If we're scanning the fallback body because scanning the real body failed, don't trigger cctor.
            // Accessing the cctor could have been a reason why we failed.
            if (!_isFallbackBodyCompilation)
            {
                // Do we need to run the cctor?
                TypeDesc owningType = runtimeDeterminedMethod.OwningType;
                if (_factory.TypeSystemContext.HasLazyStaticConstructor(owningType))
                {
                    // For beforefieldinit, we can wait for field access.
                    if (!((MetadataType)owningType).IsBeforeFieldInit)
                    {
                        // Accessing the static base will trigger the cctor.
                        if (owningType.IsRuntimeDeterminedSubtype)
                        {
                            _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.GetNonGCStaticBase, owningType), reason);
                        }
                        else
                        {
                            _dependencies.Add(_factory.ReadyToRunHelper(ReadyToRunHelperId.GetNonGCStaticBase, owningType), reason);
                        }
                    }
                }
            }

            if (opcode == ILOpcode.newobj)
            {
                TypeDesc owningType = runtimeDeterminedMethod.OwningType;
                if (owningType.IsString)
                {
                    // String .ctor handled specially below
                }
                else if (owningType.IsGCPointer)
                {
                    if (owningType.IsRuntimeDeterminedSubtype)
                    {
                        _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, owningType), reason);
                    }
                    else
                    {
                        _dependencies.Add(_factory.ConstructedTypeSymbol(owningType), reason);
                    }

                    if (owningType.IsMdArray)
                    {
                        _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.NewMultiDimArr_NonVarArg), reason);
                        return;
                    }
                    else
                    {
                        _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.NewObject), reason);
                    }
                }

                if (owningType.IsDelegate)
                {
                    // If this is a verifiable delegate construction sequence, the previous instruction is a ldftn/ldvirtftn
                    if (_previousInstructionOffset >= 0 && _ilBytes[_previousInstructionOffset] == (byte)ILOpcode.prefix1)
                    {
                        // TODO: for ldvirtftn we need to also check for the `dup` instruction, otherwise this is a normal newobj.

                        ILOpcode previousOpcode = (ILOpcode)(0x100 + _ilBytes[_previousInstructionOffset + 1]);
                        if (previousOpcode == ILOpcode.ldvirtftn || previousOpcode == ILOpcode.ldftn)
                        {
                            int delTargetToken = ReadILTokenAt(_previousInstructionOffset + 2);
                            var delTargetMethod = (MethodDesc)_methodIL.GetObject(delTargetToken);
                            TypeDesc canonDelegateType = method.OwningType.ConvertToCanonForm(CanonicalFormKind.Specific);
                            DelegateCreationInfo info = _compilation.GetDelegateCtor(canonDelegateType, delTargetMethod, previousOpcode == ILOpcode.ldvirtftn);
                            
                            if (info.NeedsRuntimeLookup)
                            {
                                _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.DelegateCtor, info), reason);
                            }
                            else
                            {
                                _dependencies.Add(_factory.ReadyToRunHelper(ReadyToRunHelperId.DelegateCtor, info), reason);
                            }

                            return;
                        }
                    }
                }
            }

            if (method.OwningType.IsDelegate && method.Name == "Invoke")
            {
                // TODO: might not want to do this if scanning for reflection.
                // This is expanded as an intrinsic, not a function call.
                return;
            }

            if (method.IsIntrinsic)
            {
                if (IsRuntimeHelpersInitializeArray(method))
                {
                    if (_previousInstructionOffset >= 0 && _ilBytes[_previousInstructionOffset] == (byte)ILOpcode.ldtoken)
                        return;
                }

                if (IsRuntimeTypeHandleGetValueInternal(method))
                {
                    if (_previousInstructionOffset >= 0 && _ilBytes[_previousInstructionOffset] == (byte)ILOpcode.ldtoken)
                        return;
                }

                if (IsActivatorDefaultConstructorOf(method))
                {
                    if (runtimeDeterminedMethod.IsRuntimeDeterminedExactMethod)
                    {
                        _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.DefaultConstructor, runtimeDeterminedMethod.Instantiation[0]), reason);
                    }
                    else
                    {
                        MethodDesc ctor = method.Instantiation[0].GetDefaultConstructor();
                        if (ctor == null)
                        {
                            MetadataType activatorType = _compilation.TypeSystemContext.SystemModule.GetKnownType("System", "Activator");
                            MetadataType classWithMissingCtor = activatorType.GetKnownNestedType("ClassWithMissingConstructor");
                            ctor = classWithMissingCtor.GetParameterlessConstructor();
                        }
                        _dependencies.Add(_factory.CanonicalEntrypoint(ctor), reason);
                    }

                    return;
                }
            }

            TypeDesc exactType = method.OwningType;

            bool resolvedConstraint = false;
            bool forceUseRuntimeLookup = false;

            MethodDesc methodAfterConstraintResolution = method;
            if (_constrained != null)
            {
                // We have a "constrained." call.  Try a partial resolve of the constraint call.  Note that this
                // will not necessarily resolve the call exactly, since we might be compiling
                // shared generic code - it may just resolve it to a candidate suitable for
                // JIT compilation, and require a runtime lookup for the actual code pointer
                // to call.

                MethodDesc directMethod = _constrained.GetClosestDefType().TryResolveConstraintMethodApprox(method.OwningType, method, out forceUseRuntimeLookup);
                if (directMethod == null && _constrained.IsEnum)
                {
                    // Constrained calls to methods on enum methods resolve to System.Enum's methods. System.Enum is a reference
                    // type though, so we would fail to resolve and box. We have a special path for those to avoid boxing.
                    directMethod = _compilation.TypeSystemContext.TryResolveConstrainedEnumMethod(_constrained, method);
                }
                
                if (directMethod != null)
                {
                    // Either
                    //    1. no constraint resolution at compile time (!directMethod)
                    // OR 2. no code sharing lookup in call
                    // OR 3. we have have resolved to an instantiating stub

                    methodAfterConstraintResolution = directMethod;

                    Debug.Assert(!methodAfterConstraintResolution.OwningType.IsInterface);
                    resolvedConstraint = true;

                    exactType = _constrained;
                }
                else if (_constrained.IsValueType)
                {
                    // We'll need to box `this`.
                    AddBoxingDependencies(_constrained, reason);
                }

                _constrained = null;
            }

            MethodDesc targetMethod = methodAfterConstraintResolution;

            bool exactContextNeedsRuntimeLookup;
            if (targetMethod.HasInstantiation)
            {
                exactContextNeedsRuntimeLookup = targetMethod.IsSharedByGenericInstantiations;
            }
            else
            {
                exactContextNeedsRuntimeLookup = exactType.IsCanonicalSubtype(CanonicalFormKind.Any);
            }

            //
            // Determine whether to perform direct call
            //

            bool directCall = false;

            if (targetMethod.Signature.IsStatic)
            {
                // Static methods are always direct calls
                directCall = true;
            }
            else if (targetMethod.OwningType.IsInterface)
            {
                // Force all interface calls to be interpreted as if they are virtual.
                directCall = false;
            }
            else if ((opcode != ILOpcode.callvirt && opcode != ILOpcode.ldvirtftn) || resolvedConstraint)
            {
                directCall = true;
            }
            else
            {
                if (!targetMethod.IsVirtual || targetMethod.IsFinal || targetMethod.OwningType.IsSealed())
                {
                    directCall = true;
                }
            }

            bool allowInstParam = opcode != ILOpcode.ldvirtftn && opcode != ILOpcode.ldftn;

            if (directCall && !allowInstParam && targetMethod.GetCanonMethodTarget(CanonicalFormKind.Specific).RequiresInstArg())
            {
                // Needs a single address to call this method but the method needs a hidden argument.
                // We need a fat function pointer for this that captures both things.

                if (exactContextNeedsRuntimeLookup)
                {
                    _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.MethodEntry, runtimeDeterminedMethod), reason);
                }
                else
                {
                    _dependencies.Add(_factory.FatFunctionPointer(runtimeDeterminedMethod), reason);
                }
            }
            else if (directCall)
            {
                bool referencingArrayAddressMethod = false;

                if (targetMethod.IsIntrinsic)
                {
                    // If this is an intrinsic method with a callsite-specific expansion, this will replace
                    // the method with a method the intrinsic expands into. If it's not the special intrinsic,
                    // method stays unchanged.
                    targetMethod = _compilation.ExpandIntrinsicForCallsite(targetMethod, _canonMethod);

                    // Array address method requires special dependency tracking.
                    referencingArrayAddressMethod = targetMethod.IsArrayAddressMethod();
                }

                MethodDesc concreteMethod = targetMethod;
                targetMethod = targetMethod.GetCanonMethodTarget(CanonicalFormKind.Specific);

                if (targetMethod.IsConstructor && targetMethod.OwningType.IsString)
                {
                    _dependencies.Add(_factory.StringAllocator(targetMethod), reason);
                }
                else if (exactContextNeedsRuntimeLookup)
                {
                    if (targetMethod.IsSharedByGenericInstantiations && !resolvedConstraint && !referencingArrayAddressMethod)
                    {
                        ISymbolNode instParam = null;

                        if (targetMethod.RequiresInstMethodDescArg())
                        {
                            instParam = GetGenericLookupHelper(ReadyToRunHelperId.MethodDictionary, runtimeDeterminedMethod);
                        }
                        else if (targetMethod.RequiresInstMethodTableArg())
                        {
                            bool hasHiddenParameter = true;

                            if (targetMethod.IsIntrinsic)
                            {
                                if (_factory.TypeSystemContext.IsSpecialUnboxingThunkTargetMethod(targetMethod))
                                    hasHiddenParameter = false;
                            }

                            if (hasHiddenParameter)
                                instParam = GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, runtimeDeterminedMethod.OwningType);
                        }

                        if (instParam != null)
                        {
                            _dependencies.Add(instParam, reason);
                        }

                        _dependencies.Add(_factory.RuntimeDeterminedMethod(runtimeDeterminedMethod), reason);
                    }
                    else
                    {
                        Debug.Assert(!forceUseRuntimeLookup);
                        _dependencies.Add(_factory.MethodEntrypoint(targetMethod), reason);
                    }
                }
                else
                {
                    ISymbolNode instParam = null;

                    if (targetMethod.RequiresInstMethodDescArg())
                    {
                        instParam = _compilation.NodeFactory.MethodGenericDictionary(concreteMethod);
                    }
                    else if (targetMethod.RequiresInstMethodTableArg() || referencingArrayAddressMethod)
                    {
                        // Ask for a constructed type symbol because we need the vtable to get to the dictionary
                        instParam = _compilation.NodeFactory.ConstructedTypeSymbol(concreteMethod.OwningType);
                    }

                    if (instParam != null)
                    {
                        _dependencies.Add(instParam, reason);

                        if (!referencingArrayAddressMethod)
                        {
                            _dependencies.Add(_compilation.NodeFactory.ShadowConcreteMethod(concreteMethod), reason);
                        }
                        else
                        {
                            // We don't want array Address method to be modeled in the generic dependency analysis.
                            // The method doesn't actually have runtime determined dependencies (won't do
                            // any generic lookups).
                            _dependencies.Add(_compilation.NodeFactory.MethodEntrypoint(targetMethod), reason);
                        }
                    }
                    else if (targetMethod.AcquiresInstMethodTableFromThis())
                    {
                        _dependencies.Add(_compilation.NodeFactory.ShadowConcreteMethod(concreteMethod), reason);
                    }
                    else
                    {
                        _dependencies.Add(_compilation.NodeFactory.MethodEntrypoint(targetMethod), reason);
                    }
                }
            }
            else if (method.HasInstantiation)
            {
                // Generic virtual method call

                if (exactContextNeedsRuntimeLookup)
                {
                    _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.MethodHandle, runtimeDeterminedMethod), reason);
                }
                else
                {
                    _dependencies.Add(_factory.RuntimeMethodHandle(runtimeDeterminedMethod), reason);
                }

                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.GVMLookupForSlot), reason);
            }
            else
            {
                ReadyToRunHelperId helper;
                if (opcode == ILOpcode.ldvirtftn)
                {
                    helper = ReadyToRunHelperId.ResolveVirtualFunction;
                }
                else
                {
                    Debug.Assert(opcode == ILOpcode.callvirt);
                    helper = ReadyToRunHelperId.VirtualCall;
                }

                if (exactContextNeedsRuntimeLookup && targetMethod.OwningType.IsInterface)
                {
                    _dependencies.Add(GetGenericLookupHelper(helper, runtimeDeterminedMethod), reason);
                }
                else
                {
                    // Get the slot defining method to make sure our virtual method use tracking gets this right.
                    // For normal C# code the targetMethod will always be newslot.
                    MethodDesc slotDefiningMethod = targetMethod.IsNewSlot ?
                        targetMethod : MetadataVirtualMethodAlgorithm.FindSlotDefiningMethodForVirtualMethod(targetMethod);

                    _dependencies.Add(_factory.ReadyToRunHelper(helper, slotDefiningMethod), reason);
                }
            }
        }

        private void ImportLdFtn(int token, ILOpcode opCode)
        {
            // Is this a verifiable delegate creation? If so, we will handle it when we reach the newobj
            if (_ilBytes[_currentOffset] == (byte)ILOpcode.newobj)
            {
                int delegateToken = ReadILTokenAt(_currentOffset + 1);
                var delegateType = ((MethodDesc)_methodIL.GetObject(delegateToken)).OwningType;
                if (delegateType.IsDelegate)
                    return;
            }

            ImportCall(opCode, token);
        }
        
        private void ImportBranch(ILOpcode opcode, BasicBlock target, BasicBlock fallthrough)
        {
            ImportFallthrough(target);

            if (fallthrough != null)
                ImportFallthrough(fallthrough);
        }

        private void ImportSwitchJump(int jmpBase, int[] jmpDelta, BasicBlock fallthrough)
        {
            for (int i = 0; i < jmpDelta.Length; i++)
            {
                BasicBlock target = _basicBlocks[jmpBase + jmpDelta[i]];
                ImportFallthrough(target);
            }

            if (fallthrough != null)
                ImportFallthrough(fallthrough);
        }

        private void ImportUnbox(int token, ILOpcode opCode)
        {
            TypeDesc type = (TypeDesc)_methodIL.GetObject(token);

            if (!type.IsValueType)
                return;

            if (type.IsRuntimeDeterminedSubtype)
            {
                _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, type), "Unbox");
            }
            else
            {
                _dependencies.Add(_factory.NecessaryTypeSymbol(type), "Unbox");
            }

            ReadyToRunHelper helper;
            if (opCode == ILOpcode.unbox)
            {
                helper = ReadyToRunHelper.Unbox;
            }
            else
            {
                Debug.Assert(opCode == ILOpcode.unbox_any);
                helper = ReadyToRunHelper.Unbox_Nullable;
            }

            _dependencies.Add(GetHelperEntrypoint(helper), "Unbox");
        }

        private void ImportRefAnyVal(int token)
        {
            // TODO
        }

        private void ImportMkRefAny(int token)
        {
            // TODO
        }

        private void ImportLdToken(int token)
        {
            object obj = _methodIL.GetObject(token);

            if (obj is TypeDesc)
            {
                var type = (TypeDesc)obj;

                // First check if this is a ldtoken Type / GetValueInternal sequence.
                BasicBlock nextBasicBlock = _basicBlocks[_currentOffset];
                if (nextBasicBlock == null)
                {
                    if ((ILOpcode)_ilBytes[_currentOffset] == ILOpcode.call)
                    {
                        int methodToken = ReadILTokenAt(_currentOffset + 1);
                        var method = (MethodDesc)_methodIL.GetObject(methodToken);
                        if (IsRuntimeTypeHandleGetValueInternal(method))
                        {
                            // Codegen expands this and doesn't do the normal ldtoken.
                            return;
                        }
                    }
                }

                if (type.IsRuntimeDeterminedSubtype)
                {
                    _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, type), "ldtoken");
                }
                else
                {
                    if (ConstructedEETypeNode.CreationAllowed(type))
                        _dependencies.Add(_factory.ConstructedTypeSymbol(type), "ldtoken");
                    else
                        _dependencies.Add(_factory.NecessaryTypeSymbol(type), "ldtoken");
                }

                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.GetRuntimeTypeHandle), "ldtoken");
            }
            else if (obj is MethodDesc)
            {
                var method = (MethodDesc)obj;
                if (method.IsRuntimeDeterminedExactMethod)
                {
                    _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.MethodHandle, method), "ldtoken");
                }
                else
                {
                    _dependencies.Add(_factory.RuntimeMethodHandle(method), "ldtoken");
                }

                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.GetRuntimeMethodHandle), "ldtoken");
            }
            else
            {
                Debug.Assert(obj is FieldDesc);

                // First check if this is a ldtoken Field / InitializeArray sequence.
                BasicBlock nextBasicBlock = _basicBlocks[_currentOffset];
                if (nextBasicBlock == null)
                {
                    if ((ILOpcode)_ilBytes[_currentOffset] == ILOpcode.call)
                    {
                        int methodToken = ReadILTokenAt(_currentOffset + 1);
                        var method = (MethodDesc)_methodIL.GetObject(methodToken);
                        if (IsRuntimeHelpersInitializeArray(method))
                        {
                            // Codegen expands this and doesn't do the normal ldtoken.
                            return;
                        }
                    }
                }

                var field = (FieldDesc)obj;
                if (field.OwningType.IsRuntimeDeterminedSubtype)
                {
                    _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.FieldHandle, field), "ldtoken");
                }
                else
                {
                    _dependencies.Add(_factory.RuntimeFieldHandle(field), "ldtoken");
                }

                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.GetRuntimeFieldHandle), "ldtoken");
            }
        }

        private void ImportRefAnyType()
        {
            // TODO
        }

        private void ImportArgList()
        {
        }

        private void ImportConstrainedPrefix(int token)
        {
            // We convert to canon, because that's what ryujit would see.
            _constrained = (TypeDesc)_methodIL.GetObject(token);
            if (_constrained.IsRuntimeDeterminedSubtype)
                _constrained = _constrained.ConvertToCanonForm(CanonicalFormKind.Specific);
        }

        private void ImportFieldAccess(int token, bool isStatic, string reason)
        {
            if (isStatic)
            {
                var field = (FieldDesc)_methodIL.GetObject(token);

                ReadyToRunHelperId helperId;
                if (field.IsThreadStatic)
                {
                    helperId = ReadyToRunHelperId.GetThreadStaticBase;
                }
                else if (field.HasGCStaticBase)
                {
                    helperId = ReadyToRunHelperId.GetGCStaticBase;
                }
                else
                {
                    Debug.Assert(field.IsStatic);
                    helperId = ReadyToRunHelperId.GetNonGCStaticBase;
                }

                TypeDesc owningType = field.OwningType;
                if (owningType.IsRuntimeDeterminedSubtype)
                {
                    _dependencies.Add(GetGenericLookupHelper(helperId, owningType), reason);
                }
                else
                {
                    _dependencies.Add(_factory.ReadyToRunHelper(helperId, owningType), reason);
                }
            }
        }

        private void ImportLoadField(int token, bool isStatic)
        {
            ImportFieldAccess(token, isStatic, isStatic ? "ldsfld" : "ldfld");
        }

        private void ImportAddressOfField(int token, bool isStatic)
        {
            ImportFieldAccess(token, isStatic, isStatic ? "ldsflda" : "ldflda");
        }

        private void ImportStoreField(int token, bool isStatic)
        {
            ImportFieldAccess(token, isStatic, isStatic ? "stsfld" : "stfld");
        }

        private void ImportLoadString(int token)
        {
            // If we care, this can include allocating the frozen string node.
            _dependencies.Add(_factory.SerializedStringObject(""), "ldstr");
        }

        private void ImportBox(int token)
        {
            AddBoxingDependencies((TypeDesc)_methodIL.GetObject(token), "Box");
        }

        private void AddBoxingDependencies(TypeDesc type, string reason)
        {
            if (type.IsRuntimeDeterminedSubtype)
            {
                _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, type), reason);
            }
            else
            {
                _dependencies.Add(_factory.ConstructedTypeSymbol(type), reason);
            }

            if (type.IsNullable)
            {
                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.Box), reason);
            }
            else
            {
                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.Box_Nullable), reason);
            }
        }

        private void ImportLeave(BasicBlock target)
        {
            ImportFallthrough(target);
        }

        private void ImportNewArray(int token)
        {
            var type = ((TypeDesc)_methodIL.GetObject(token)).MakeArrayType();
            if (type.IsRuntimeDeterminedSubtype)
            {
                _dependencies.Add(GetGenericLookupHelper(ReadyToRunHelperId.TypeHandle, type), "newarr");
                _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.NewArray), "newarr");
            }
            else
            {
                _dependencies.Add(_factory.ReadyToRunHelper(ReadyToRunHelperId.NewArr1, type), "newarr");
            }
        }

        private void ImportLoadElement(int token)
        {
            _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.RngChkFail), "ldelem");
        }

        private void ImportLoadElement(TypeDesc elementType)
        {
            _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.RngChkFail), "ldelem");
        }

        private void ImportStoreElement(int token)
        {
            _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.RngChkFail), "stelem");
        }

        private void ImportStoreElement(TypeDesc elementType)
        {
            _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.RngChkFail), "stelem");
        }

        private void ImportAddressOfElement(int token)
        {
            _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.RngChkFail), "ldelema");
        }

        private void ImportBinaryOperation(ILOpcode opcode)
        {
            switch (opcode)
            {
                case ILOpcode.add_ovf:
                case ILOpcode.add_ovf_un:
                case ILOpcode.mul_ovf:
                case ILOpcode.mul_ovf_un:
                case ILOpcode.sub_ovf:
                case ILOpcode.sub_ovf_un:
                    _dependencies.Add(GetHelperEntrypoint(ReadyToRunHelper.Overflow), "_ovf");
                    break;
            }
        }

        private void ImportFallthrough(BasicBlock next)
        {
            MarkBasicBlock(next);
        }

        private int ReadILTokenAt(int ilOffset)
        {
            return (int)(_ilBytes[ilOffset] 
                + (_ilBytes[ilOffset + 1] << 8)
                + (_ilBytes[ilOffset + 2] << 16)
                + (_ilBytes[ilOffset + 3] << 24));
        }

        private bool IsRuntimeHelpersInitializeArray(MethodDesc method)
        {
            if (method.IsIntrinsic && method.Name == "InitializeArray")
            {
                MetadataType owningType = method.OwningType as MetadataType;
                if (owningType != null)
                {
                    return owningType.Name == "RuntimeHelpers" && owningType.Namespace == "System.Runtime.CompilerServices";
                }
            }

            return false;
        }

        private bool IsRuntimeTypeHandleGetValueInternal(MethodDesc method)
        {
            if (method.IsIntrinsic && method.Name == "GetValueInternal")
            {
                MetadataType owningType = method.OwningType as MetadataType;
                if (owningType != null)
                {
                    return owningType.Name == "RuntimeTypeHandle" && owningType.Namespace == "System";
                }
            }

            return false;
        }

        private bool IsActivatorDefaultConstructorOf(MethodDesc method)
        {
            if (method.IsIntrinsic && method.Name == "DefaultConstructorOf" && method.Instantiation.Length == 1)
            {
                MetadataType owningType = method.OwningType as MetadataType;
                if (owningType != null)
                {
                    return owningType.Name == "Activator" && owningType.Namespace == "System";
                }
            }

            return false;
        }

        private TypeDesc GetWellKnownType(WellKnownType wellKnownType)
        {
            return _compilation.TypeSystemContext.GetWellKnownType(wellKnownType);
        }

        private void ImportNop() { }
        private void ImportBreak() { }
        private void ImportLoadVar(int index, bool argument) { }
        private void ImportStoreVar(int index, bool argument) { }
        private void ImportAddressOfVar(int index, bool argument) { }
        private void ImportDup() { }
        private void ImportPop() { }
        private void ImportCalli(int token) { }
        private void ImportLoadNull() { }
        private void ImportReturn() { }
        private void ImportLoadInt(long value, StackValueKind kind) { }
        private void ImportLoadFloat(double value) { }
        private void ImportLoadIndirect(int token) { }
        private void ImportLoadIndirect(TypeDesc type) { }
        private void ImportStoreIndirect(int token) { }
        private void ImportStoreIndirect(TypeDesc type) { }
        private void ImportShiftOperation(ILOpcode opcode) { }
        private void ImportCompareOperation(ILOpcode opcode) { }
        private void ImportConvert(WellKnownType wellKnownType, bool checkOverflow, bool unsigned) { }
        private void ImportUnaryOperation(ILOpcode opCode) { }
        private void ImportCpOpj(int token) { }
        private void ImportCkFinite() { }
        private void ImportLocalAlloc() { }
        private void ImportEndFilter() { }
        private void ImportCpBlk() { }
        private void ImportInitBlk() { }
        private void ImportRethrow() { }
        private void ImportSizeOf(int token) { }
        private void ImportUnalignedPrefix(byte alignment) { }
        private void ImportVolatilePrefix() { }
        private void ImportTailPrefix() { }
        private void ImportNoPrefix(byte mask) { }
        private void ImportReadOnlyPrefix() { }
        private void ImportThrow() { }
        private void ImportInitObj(int token) { }
        private void ImportLoadLength() { }
        private void ImportEndFinally() { }
    }
}
