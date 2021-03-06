// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.Augments
{
    /// <summary>For internal use only.  Exposes runtime functionality to the Environments implementation in corefx.</summary>
    public static partial class EnvironmentAugments
    {
        public static string GetEnvironmentVariable(string variable)
        {
            if (variable == null)
                throw new ArgumentNullException(nameof(variable));
            return GetEnvironmentVariableCore(variable);
        }

        public static string GetEnvironmentVariable(string variable, EnvironmentVariableTarget target)
        {
            if (target == EnvironmentVariableTarget.Process)
                return GetEnvironmentVariable(variable);

            if (variable == null)
                throw new ArgumentNullException(nameof(variable));

            bool fromMachine = ValidateAndConvertRegistryTarget(target);
            return GetEnvironmentVariableFromRegistry(variable, fromMachine: fromMachine);
        }

        public static void SetEnvironmentVariable(string variable, string value)
        {
            ValidateVariableAndValue(variable, ref value);

            SetEnvironmentVariableCore(variable, value);
        }

        public static void SetEnvironmentVariable(string variable, string value, EnvironmentVariableTarget target)
        {
            if (target == EnvironmentVariableTarget.Process)
            {
                SetEnvironmentVariable(variable, value);
                return;
            }

            ValidateVariableAndValue(variable, ref value);

            bool fromMachine = ValidateAndConvertRegistryTarget(target);
            SetEnvironmentVariableFromRegistry(variable, value, fromMachine: fromMachine);
        }

        private static void ValidateVariableAndValue(string variable, ref string value)
        {
            const int MaxEnvVariableValueLength = 32767;

            if (variable == null)
                throw new ArgumentNullException(nameof(variable));

            if (variable.Length == 0)
                throw new ArgumentException(SR.Argument_StringZeroLength, nameof(variable));

            if (variable[0] == '\0')
                throw new ArgumentException(SR.Argument_StringFirstCharIsZero, nameof(variable));

            if (variable.Length >= MaxEnvVariableValueLength)
                throw new ArgumentException(SR.Argument_LongEnvVarValue, nameof(variable));

            if (variable.IndexOf('=') != -1)
                throw new ArgumentException(SR.Argument_IllegalEnvVarName, nameof(variable));

            if (string.IsNullOrEmpty(value) || value[0] == '\0')
            {
                // Explicitly null out value if it's empty
                value = null;
            }
            else if (value.Length >= MaxEnvVariableValueLength)
            {
                throw new ArgumentException(SR.Argument_LongEnvVarValue, nameof(value));
            }
        }

        // TODO Perf: Once CoreCLR gets EnumerateEnvironmentVariables(), get rid of GetEnvironmentVariables() and have 
        // corefx call EnumerateEnvironmentVariables() instead so we don't have to create a dictionary just to copy it into
        // another dictionary.
        public static IDictionary GetEnvironmentVariables() => new Dictionary<string, string>(EnumerateEnvironmentVariables());
        public static IDictionary GetEnvironmentVariables(EnvironmentVariableTarget target) => new Dictionary<string, string>(EnumerateEnvironmentVariables(target));

        public static IEnumerable<KeyValuePair<string, string>> EnumerateEnvironmentVariables(EnvironmentVariableTarget target)
        {
            if (target == EnvironmentVariableTarget.Process)
                return EnumerateEnvironmentVariables();

            bool fromMachine = ValidateAndConvertRegistryTarget(target);
            return EnumerateEnvironmentVariablesFromRegistry(fromMachine: fromMachine);
        }

        private static bool ValidateAndConvertRegistryTarget(EnvironmentVariableTarget target)
        {
            Debug.Assert(target != EnvironmentVariableTarget.Process);
            if (target == EnvironmentVariableTarget.Machine)
                return true;
            else if (target == EnvironmentVariableTarget.User)
                return false;
            else
                throw new ArgumentOutOfRangeException(nameof(target), target, SR.Format(SR.Arg_EnumIllegalVal, target));
        }
    }
}
