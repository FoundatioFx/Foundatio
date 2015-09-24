namespace Foundatio.Utility {
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>Contains helper methods for the cloners</summary>
    internal static class ClonerHelpers {
        /// <summary>
        ///     Returns all the fields of a type, working around a weird reflection issue
        ///     where explicitly declared fields in base classes are returned, but not
        ///     automatic property backing fields.
        /// </summary>
        /// <param name="type">Type whose fields will be returned</param>
        /// <param name="bindingFlags">Binding flags to use when querying the fields</param>
        /// <returns>All of the type's fields, including its base types</returns>
        public static FieldInfo[] GetFieldInfosIncludingBaseClasses(
            Type type, BindingFlags bindingFlags
            ) {
            FieldInfo[] fieldInfos = type.GetFields(bindingFlags);

            // If this class doesn't have a base, don't waste any time
            if (type.BaseType == typeof(object)) {
                return fieldInfos;
            }
            // Otherwise, collect all types up to the furthest base class
            var fieldInfoList = new List<FieldInfo>(fieldInfos);
            while (type.BaseType != typeof(object)) {
                type = type.BaseType;
                fieldInfos = type.GetFields(bindingFlags);

                // Look for fields we do not have listed yet and merge them into the main list
                for (int index = 0; index < fieldInfos.Length; ++index) {
                    bool found = false;

                    for (int searchIndex = 0; searchIndex < fieldInfoList.Count; ++searchIndex) {
                        bool match =
                            (fieldInfoList[searchIndex].DeclaringType == fieldInfos[index].DeclaringType) &&
                            (fieldInfoList[searchIndex].Name == fieldInfos[index].Name);

                        if (match) {
                            found = true;
                            break;
                        }
                    }

                    if (!found) {
                        fieldInfoList.Add(fieldInfos[index]);
                    }
                }
            }

            return fieldInfoList.ToArray();
        }
    }
}