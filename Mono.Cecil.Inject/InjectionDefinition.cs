﻿using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil.Cil;

namespace Mono.Cecil.Inject
{
    /// <summary>
    ///     The core class containing all the injector methods.
    /// </summary>
    public class InjectionDefinition
    {
        internal InjectionDefinition() {}

        /// <summary>
        ///     Attempts to construct an instance of <see cref="InjectionDefinition" /> by linking the injection method with the
        ///     injection target (the method to be injected).
        ///     The way how the method is injected is specified by the injection flags. If the injection method does not match the
        ///     criteria set by the injection flags, an exception will be thrown.
        /// </summary>
        /// <param name="injectTarget">The method that will be injected.</param>
        /// <param name="injectMethod">The method which to inject.</param>
        /// <param name="flags">Injection flags that specify what values to pass to the injection method and how to inject it.</param>
        /// <param name="localVarIDs">
        ///     An array of indicies of local variables to pass to the injection method. Used only if
        ///     <see cref="InjectFlags.PassLocals" /> is specified, otherwise ignored.
        /// </param>
        /// <param name="memberReferences">
        ///     An array of class fields from the type the target lies in to pass to the injection
        ///     method. Used only if <see cref="InjectFlags.PassFields" /> is specified, otherwise ignored.
        /// </param>
        public InjectionDefinition(MethodDefinition injectTarget,
                                   MethodDefinition injectMethod,
                                   InjectFlags flags,
                                   int[] localVarIDs = null,
                                   params FieldDefinition[] memberReferences)
        {
            Assert(
            injectMethod.IsStatic,
            $"{nameof(injectMethod)} must be static in order to be used as the injection.");
            Assert(injectMethod.IsPublic, $"{nameof(injectMethod)} must be public.");
            Assert(
            injectMethod.HasBody,
            $"{nameof(injectMethod)} must have a definition in order to be used as the injecton.");

            InjectValues hFlags = flags.ToValues();

            bool isVoid = injectTarget.ReturnType.FullName == "System.Void";

            Assert(
            !hFlags.PassLocals || localVarIDs != null,
            $"Supposed to pass local references, but {nameof(localVarIDs)} is empty");
            Assert(
            !hFlags.PassFields || memberReferences != null,
            $"Supposed to pass member fields, but {nameof(memberReferences)} is empty!");

            int prefixCount = Convert.ToInt32(hFlags.PassTag) + Convert.ToInt32(hFlags.PassInvokingInstance)
                              + Convert.ToInt32(hFlags.ModifyReturn && !isVoid);
            int localsCount = hFlags.PassLocals ? localVarIDs.Length : 0;
            int memberRefCount = (hFlags.PassFields ? memberReferences.Length : 0);
            int paramCount = (hFlags.PassParameters ? injectTarget.Parameters.Count : 0);
            int parameters = injectMethod.Parameters.Count - prefixCount - localsCount - memberRefCount;
            // int parameters = injectMethod.Parameters.Count - prefixCount - localsCount - memberRefCount;
            // PassParameters => 0 < parameters <= injectTarget.Patameters.Count
            // !PassParameters => parameters = 0
            Assert(
            (hFlags.PassParameters && 0 < parameters && parameters <= injectTarget.Parameters.Count
             || !hFlags.PassParameters && parameters == 0),
            //injectMethod.Parameters.Count == prefixCount + localsCount + memberRefCount + paramCount,
            $@"The injection method has a wrong number of parameters! Check that the provided target method, local variables, member references and injection flags add up to the right number of parameters.
Needed parameters: Prefix: {
            prefixCount}, Locals: {localsCount}, Members: {memberRefCount}, Parameters: {
            (hFlags.PassParameters ? "between 1 and " + paramCount : "0")}, TOTAL: {
            (hFlags.PassParameters ? $"between {prefixCount + localsCount + memberRefCount + 1} and " : "")}{
            prefixCount + localsCount + memberRefCount + paramCount}.
Injection has {injectMethod.Parameters.Count
            } parameters.");

            TypeComparer comparer = new TypeComparer();

            if (hFlags.PassTag)
            {
                Assert(
                injectMethod.Parameters[0].ParameterType.FullName == "System.Int32",
                "Supposed to pass a tag, but the provided tag must be of type System.Int32 (int).");
            }

            if (hFlags.PassInvokingInstance)
            {
                Assert(
                !injectTarget.IsStatic,
                "Supposed to pass invoking instance, but the target method is static and thus is not bound to any instances.");
                Assert(
                comparer.Equals(
                injectMethod.Parameters[Convert.ToInt32(hFlags.PassTag)].ParameterType,
                injectTarget.DeclaringType),
                "Supposed to pass invoking instance, but the type of the instance does not match with the type declared in the injection method.");
            }

            if (hFlags.ModifyReturn)
            {
                Assert(
                injectMethod.ReturnType.FullName == "System.Boolean",
                "The injection method must return a boolean in order to alter the return value.");
                Assert(
                isVoid
                || comparer.Equals(
                injectMethod.Parameters[Convert.ToInt32(hFlags.PassTag) + Convert.ToInt32(hFlags.PassInvokingInstance)]
                   .ParameterType,
                new ByReferenceType(injectTarget.ReturnType)),
                "Supposed to modify the return value, but the provided return type does not match with the return type of the target method! Also make sure the type is passed by reference (out/ref).");
            }

            if (hFlags.PassLocals)
            {
                Assert(injectTarget.Body.HasVariables, "The target method does not have any locals.");
                Assert(
                localVarIDs.Length != 0,
                "Supposed to pass method locals, but the IDs of the locals were not specified.");
                Assert(
                localVarIDs.All(i => 0 <= i && i < injectTarget.Body.Variables.Count),
                "Supposed to receive local references, but the provided local variable index/indices do not exist in the target method!");
                Assert(
                injectMethod.Parameters.Slice(prefixCount, localsCount)
                            .Select((p, i) => new {param = p, index = i})
                            .All(
                            t =>
                            t.param.ParameterType.IsByReference
                            && comparer.Equals(
                            t.param.ParameterType,
                            new ByReferenceType(injectTarget.Body.Variables[t.index].VariableType))),
                "Supposed to receive local references, but the types between injection method and target method mismatch. Also make sure they are passed by reference (ref/out).");
            }

            if (hFlags.PassFields)
            {
                Assert(!injectTarget.IsStatic, "Cannot pass member references if the injection method is static!");
                Assert(
                memberReferences.Length != 0,
                "Supposed to pass member references, but no members were specified.");
                Assert(
                memberReferences.All(
                m =>
                m.DeclaringType.FullName == injectTarget.DeclaringType.FullName
                && m.DeclaringType.BaseType.FullName == injectTarget.DeclaringType.BaseType.FullName),
                $"The provided member fields do not belong to {injectTarget.DeclaringType}");

                IEnumerable<TypeReference> paramRefs =
                injectMethod.Parameters.Slice(prefixCount + localsCount, memberRefCount).Select(p => p.ParameterType);
                IEnumerable<TypeReference> typeReferences = paramRefs as TypeReference[] ?? paramRefs.ToArray();

                Assert(
                typeReferences.All(p => p.IsByReference),
                "Supposed to pass class members, but the provided parameters in the injection method are not of a reference type (ref)!");
                Assert(
                typeReferences.SequenceEqual(
                memberReferences.Select(f => (TypeReference) new ByReferenceType(f.FieldType)),
                comparer),
                "Supposed to pass class members, but the existing members are of a different type than the ones specified in the injection method.");
            }

            if (hFlags.PassParameters)
            {
                Assert(
                injectMethod.HasGenericParameters == injectTarget.HasGenericParameters,
                "The injection and target methods have mismatching specification of generic parameters!");

                Assert(
                !injectMethod.HasGenericParameters
                || injectMethod.GenericParameters.Count <= injectTarget.GenericParameters.Count,
                "The injection and target methods have a mismatching number of generic parameters! The injection method must have less or the same number of generic paramters as the target!");

                Assert(
                !hFlags.PassParametersByRef
                || injectMethod.Parameters.Skip(prefixCount + localsCount + memberRefCount)
                               .All(p => p.ParameterType.IsByReference),
                "Supposed to pass target method parameters by reference, but the provided parameters in the injection method are not of a reference type (ref).");

                Assert(
                injectMethod.Parameters.Skip(prefixCount + localsCount + memberRefCount)
                            .Select(p => p.ParameterType)
                            .SequenceEqual(
                            injectTarget.Parameters.Take(parameters)
                                        .Select(
                                        p =>
                                        (hFlags.PassParametersByRef
                                         ? new ByReferenceType(p.ParameterType) : p.ParameterType)),
                            comparer),
                "Supposed to pass target method parameters by reference, but the types specified in injection and target methods do not match.");
            }

            InjectMethod = injectMethod;
            InjectTarget = injectTarget;
            Flags = flags;
            MemberReferences = memberReferences;
            LocalVarIDs = localVarIDs;
            _PrefixCount = prefixCount;
            _MemeberRefCount = memberRefCount;
            _ParameterCount = parameters;
        }

        internal int _MemeberRefCount { get; set; }
        internal int _ParameterCount { get; set; }
        internal int _PrefixCount { get; set; }

        /// <summary>
        ///     Flags that specify how the injection will be performed and which parameters are to be passed to the injection
        ///     method.
        /// </summary>
        public InjectFlags Flags { get; internal set; }

        /// <summary>
        ///     The injection method; the method the call of which will be injected into the target.
        /// </summary>
        public MethodDefinition InjectMethod { get; internal set; }

        /// <summary>
        ///     The injection target-
        /// </summary>
        public MethodDefinition InjectTarget { get; internal set; }

        /// <summary>
        ///     An array of indicies of local variables to pass to the injection method. Used only if
        ///     <see cref="InjectFlags.PassLocals" /> is specified, otherwise null.
        /// </summary>
        public int[] LocalVarIDs { get; internal set; }

        /// <summary>
        ///     An array of class fields from the type the target lies in to pass to the injection method. Used only if
        ///     <see cref="InjectFlags.PassFields" /> is specified, otherwise null.
        /// </summary>
        public FieldDefinition[] MemberReferences { get; internal set; }

        internal void Assert(bool val, string message)
        {
            if (!val)
                throw new InjectionDefinitionException(message);
        }

        /// <summary>
        ///     Inject the call of the injection method into the target.
        /// </summary>
        /// <param name="startCode">
        ///     The index of the instruction from which to start injecting. If positive, will count from the
        ///     beginning of the method. If negative, will count from the end. For instance, -1 is the method's last instruction
        ///     and 0 is the first.
        /// </param>
        /// <param name="token">
        ///     If <see cref="InjectFlags.PassTag" /> is specified, the value of this parameter will be passed as a
        ///     parameter to the injection method.
        /// </param>
        /// <param name="direction">The direction in which to insert the call: either above the start code or below it.</param>
        public void Inject(int startCode = 0, int token = 0, InjectDirection direction = InjectDirection.Before)
        {
            startCode = startCode < 0 ? InjectTarget.Body.Instructions.Count + startCode : startCode;
            Inject(InjectTarget.Body.Instructions[startCode], token, direction);
        }

        /// <summary>
        ///     Inject the call of the injection method into the target.
        /// </summary>
        /// <param name="startCode">The instruction from which to start injecting.</param>
        /// <param name="token">
        ///     If <see cref="InjectFlags.PassTag" /> is specified, the value of this parameter will be passed as a
        ///     parameter to the injection method.
        /// </param>
        /// <param name="direction">The direction in which to insert the call: either above the start code or below it.</param>
        public void Inject(Instruction startCode, int token = 0, InjectDirection direction = InjectDirection.Before)
        {
            InjectValues flags = Flags.ToValues();

#if DEBUG
            Logger.LogLine(LogMask.Inject, "##### INJECTION START #####");
            Logger.LogLine(
            LogMask.Inject,
            $"Injecting a call to {InjectMethod.Module.Name}.{InjectMethod.Name} into {InjectTarget.Module.Name}.{InjectTarget.Name}.");
            Logger.LogLine(LogMask.Inject, "Patch parameters:");
            Logger.LogLine(LogMask.Inject, $"Pass tag: {flags.PassTag}");
            Logger.LogLine(LogMask.Inject, $"Modify return value: {flags.ModifyReturn}");
            Logger.LogLine(LogMask.Inject, $"Pass THIS: {flags.PassInvokingInstance}");
            Logger.LogLine(LogMask.Inject, $"Pass method locals: {flags.PassLocals}");
            Logger.LogLine(LogMask.Inject, $"Pass member fields: {flags.PassFields}");
            Logger.LogLine(LogMask.Inject, $"Pass member parameters: {flags.PassParameters}");
            if (flags.PassParameters)
            {
                Logger.LogLine(
                LogMask.Inject,
                $"Member parameters are passed by {(flags.PassParametersByRef ? "reference" : "value")}");
            }
#endif

            bool isVoid = InjectTarget.ReturnType.FullName == "System.Void";

            MethodReference hookRef = InjectTarget.Module.Import(InjectMethod);

            MethodBody targetBody = InjectTarget.Body;
            ILProcessor il = targetBody.GetILProcessor();
            int startIndex = targetBody.Instructions.IndexOf(startCode);
            if (startIndex == -1)
                throw new ArgumentOutOfRangeException(nameof(startCode));
            Instruction startInstruction = startCode;

            if (direction == InjectDirection.Before && startIndex != 0)
            {
                Instruction oldIns = ILUtils.CopyInstruction(startCode);
                ILUtils.ReplaceInstruction(startCode, il.Create(OpCodes.Nop));
                Instruction ins = targetBody.Instructions[startIndex];
                il.InsertAfter(ins, oldIns);
                startInstruction = targetBody.Instructions[startIndex + 1];
            }
            else if (direction == InjectDirection.After)
            {
                il.InsertAfter(startCode, il.Create(OpCodes.Nop));
                startInstruction = targetBody.Instructions[startIndex + 1];
            }

            VariableDefinition returnDef = null;
            if (flags.ModifyReturn && !isVoid)
            {
                targetBody.InitLocals = true;
                returnDef = new VariableDefinition(
                InjectMethod.Name + "_return",
                InjectTarget.ReturnType);
                targetBody.Variables.Add(returnDef);
            }


            if (flags.PassTag)
            {
                Logger.LogLine(LogMask.Inject, $"Passing custom token value: {token}");
                il.InsertBefore(startInstruction, il.Create(OpCodes.Ldc_I4, token));
            }
            if (flags.PassInvokingInstance)
            {
                Logger.LogLine(LogMask.Inject, "Passing THIS argument");
                il.InsertBefore(startInstruction, il.Create(OpCodes.Ldarg_0));
            }
            if (flags.ModifyReturn && !isVoid)
            {
                Logger.LogLine(LogMask.Inject, "Passing return reference");
                il.InsertBefore(startInstruction, il.Create(OpCodes.Ldloca_S, returnDef));
            }
            if (flags.PassLocals)
            {
                Logger.LogLine(LogMask.Inject, "Passing local variable references");
                foreach (int i in LocalVarIDs)
                {
                    Logger.LogLine(LogMask.Inject, $"Passing local variable index: {i}");
                    il.InsertBefore(startInstruction, il.Create(OpCodes.Ldloca_S, (byte) i));
                }
            }
            if (flags.PassFields)
            {
                Logger.LogLine(LogMask.Inject, "Passing member field references");
                IEnumerable<FieldReference> memberRefs = MemberReferences.Select(t => t.Module.Import(t));
                foreach (FieldReference t in memberRefs)
                {
                    Logger.LogLine(LogMask.Inject, $"Passing member field {t.FullName}");
                    il.InsertBefore(startInstruction, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(startInstruction, il.Create(OpCodes.Ldflda, t));
                }
            }
            if (flags.PassParameters)
            {
                Logger.LogLine(
                LogMask.Inject,
                $"Passing member parameters by {(flags.PassParametersByRef ? "reference" : "value")}");
                int icr = Convert.ToInt32(!InjectTarget.IsStatic);
                for (int i = 0; i < _ParameterCount; i++)
                {
                    Logger.LogLine(LogMask.Inject, $"Passing parameter of index {(i + icr)}");
                    il.InsertBefore(
                    startInstruction,
                    flags.PassParametersByRef
                    ? il.Create(OpCodes.Ldarga_S, (byte) (i + icr)) : il.Create(OpCodes.Ldarg_S, (byte) (i + icr)));
                }
            }
            Logger.LogLine(LogMask.Inject, "Injecting the call to the method");
            il.InsertBefore(startInstruction, il.Create(OpCodes.Call, hookRef));
            if (flags.ModifyReturn)
            {
                Logger.LogLine(LogMask.Inject, "Inserting return check");
                il.InsertBefore(startInstruction, il.Create(OpCodes.Brfalse_S, startInstruction));
                if (!isVoid)
                {
                    Logger.LogLine(LogMask.Inject, "Inserting return value");
                    il.InsertBefore(startInstruction, il.Create(OpCodes.Ldloc_S, returnDef));
                }
                Logger.LogLine(LogMask.Inject, "Inserting return command");
                il.InsertBefore(startInstruction, il.Create(OpCodes.Ret));
            }
            // If we don't use the return value of InjectMethod, pop it from the ES
            else if (InjectMethod.ReturnType.FullName != "System.Void")
                il.InsertBefore(startInstruction, il.Create(OpCodes.Pop));
            if (direction == InjectDirection.After)
                il.Remove(startInstruction);
            Logger.LogLine(LogMask.Inject, "Injection complete");
            Logger.LogLine(LogMask.Inject, "##### INJECTION END #####");
        }
    }
}