#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Net;
namespace Foundatio.FastCloner.Code;

internal static class FastClonerExprGenerator
{
    internal static readonly ConcurrentDictionary<Type, Func<Type, bool, ExpressionPosition, object>> CustomTypeHandlers = [];
    private static readonly ConcurrentDictionary<FieldInfo, bool> readonlyFields = new ConcurrentDictionary<FieldInfo, bool>();
    private static readonly MethodInfo fieldSetMethod;
    private static readonly Lazy<MethodInfo> isTypeIgnoredMethodInfo = new Lazy<MethodInfo>(() => typeof(FastClonerCache).GetMethod(nameof(FastClonerCache.IsTypeIgnored), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, [typeof(Type)], null)!, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static MethodInfo IsTypeIgnoredMethodInfo => isTypeIgnoredMethodInfo.Value;

    static FastClonerExprGenerator()
    {
        fieldSetMethod = typeof(FieldInfo).GetMethod(nameof(FieldInfo.SetValue), [typeof(object), typeof(object)])!;
    }

    internal static object? GenerateClonerInternal(Type realType, bool asObject) => GenerateProcessMethod(realType, asObject && realType.IsValueType());

    private static bool MemberIsIgnored(MemberInfo memberInfo)
    {
        return GetMemberBehavior(memberInfo) == CloneBehavior.Ignore;
    }

    internal static bool MemberIsShallow(MemberInfo memberInfo)
    {
        return GetMemberBehavior(memberInfo) == CloneBehavior.Shallow;
    }
    
    internal static bool MemberIsReference(MemberInfo memberInfo)
    {
        return GetMemberBehavior(memberInfo) == CloneBehavior.Reference;
    }
    
    /// <summary>
    /// Returns true if the member should have its reference copied directly without deep cloning.
    /// This applies to both Shallow and Reference behaviors.
    /// </summary>
    internal static bool MemberShouldCopyReference(MemberInfo memberInfo)
    {
        CloneBehavior? behavior = GetMemberBehavior(memberInfo);
        return behavior is CloneBehavior.Shallow or CloneBehavior.Reference;
    }
    
    /// <summary>
    /// Gets the clone behavior for a type by checking for FastClonerBehaviorAttribute on the type definition.
    /// </summary>
    internal static CloneBehavior? GetTypeBehavior(Type type)
    {
        FastClonerBehaviorAttribute? behaviorAttr = type.GetCustomAttribute<FastClonerBehaviorAttribute>();
        return behaviorAttr?.Behavior;
    }
    
    /// <summary>
    /// Gets the clone behavior for a member by checking:
    /// 1. Member-level FastClonerBehaviorAttribute (highest priority)
    /// 2. [NonSerialized] attribute (treat as Ignore)
    /// 3. Backing field's corresponding property
    /// 4. Type-level FastClonerBehaviorAttribute on the member's type (lowest priority)
    /// </summary>
    internal static CloneBehavior? GetMemberBehavior(MemberInfo memberInfo)
    {
        return FastClonerCache.GetOrAddMemberBehavior(memberInfo, mi =>
        {
            // 1. Check for member-level FastClonerBehaviorAttribute (base class covers all derived attributes)
            FastClonerBehaviorAttribute? behaviorAttr = mi.GetCustomAttribute<FastClonerBehaviorAttribute>();
            if (behaviorAttr is not null)
                return behaviorAttr.Behavior;

            // 2. Check [NonSerialized] (treat as Ignore)
            NonSerializedAttribute? nonSerialized = mi.GetCustomAttribute<NonSerializedAttribute>();
            if (nonSerialized is not null)
                return CloneBehavior.Ignore;

            // 3. For backing fields of auto-implemented properties, check the corresponding property
            // Backing fields are named like "<PropertyName>k__BackingField"
            if (mi is FieldInfo field && field.Name.StartsWith("<") && field.Name.EndsWith(">k__BackingField"))
            {
                string propertyName = field.Name.Substring(1, field.Name.Length - ">k__BackingField".Length - 1);
                // Use DeclaredOnly to avoid AmbiguousMatchException when property is hidden in derived class
                PropertyInfo? property = field.DeclaringType?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    FastClonerBehaviorAttribute? propBehavior = property.GetCustomAttribute<FastClonerBehaviorAttribute>();
                    if (propBehavior is not null)
                        return propBehavior.Behavior;
                }
            }

            // 4. Check for type-level attribute on the member's type
            Type? memberType = mi switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo p => p.PropertyType,
                EventInfo e => e.EventHandlerType,
                _ => null
            };
            
            if (memberType != null)
            {
                CloneBehavior? typeBehavior = GetTypeBehavior(memberType);
                if (typeBehavior.HasValue)
                    return typeBehavior.Value;
            }

            return null;
        });
    }


    internal static bool CalculateTypeContainsIgnoredMembers(Type type)
    {
        IEnumerable<MemberInfo> members = FastClonerCache.GetOrAddAllMembers(type, GetAllMembers);

        return members.Any(MemberIsIgnored);
    }

    private static bool TypeHasReadonlyFields(Type type)
    {
        return FastClonerCache.GetOrAddAllMembers(type, GetAllMembers)
            .OfType<FieldInfo>()
            .Any(f => f.IsInitOnly);
    }

    private static void AddStructReadonlyFieldsCloneExpressions(
        List<Expression> expressionList,
        ParameterExpression fromLocal,
        ParameterExpression boxedToLocal,
        ParameterExpression state,
        Type type)
    {
        IEnumerable<MemberInfo> members = FastClonerCache.GetOrAddAllMembers(type, GetAllMembers);
        Dictionary<string, Type> ignoredEventDetails = FastClonerCache.GetOrAddIgnoredEventInfo(type, t =>
        {
            Dictionary<string, Type> details = new Dictionary<string, Type>();
            EventInfo[] events = t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (EventInfo evtInfo in events)
            {
                if (MemberIsIgnored(evtInfo))
                {
                    details[evtInfo.Name] = evtInfo.EventHandlerType;
                }
            }

            return details;
        });

        foreach (MemberInfo member in members)
        {
            if (member is not FieldInfo fieldInfo || !fieldInfo.IsInitOnly)
            {
                continue;
            }

            Type memberType = fieldInfo.FieldType;
            bool shouldBeIgnored = false;

            if (MemberIsIgnored(member))
            {
                shouldBeIgnored = true;
            }
            else if (ignoredEventDetails.TryGetValue(fieldInfo.Name, out Type? evtType))
            {
                if (evtType == memberType)
                {
                    shouldBeIgnored = true;
                }
            }
            else if (FastClonerCache.IsTypeIgnored(memberType))
            {
                shouldBeIgnored = true;
            }

            if (shouldBeIgnored)
            {
                expressionList.Add(Expression.Call(
                    Expression.Constant(fieldInfo),
                    fieldSetMethod,
                    boxedToLocal,
                    Expression.Convert(Expression.Default(memberType), typeof(object))
                ));
                continue;
            }

            if (FastClonerSafeTypes.CanReturnSameObject(memberType))
            {
                continue;
            }

            // Check if member should copy reference directly (Shallow or Reference behavior)
            if (MemberShouldCopyReference(member))
            {
                // For shallow/reference clone, just copy the reference/value directly
                Expression originalValue = Expression.MakeMemberAccess(fromLocal, member);
                expressionList.Add(Expression.Call(
                    Expression.Constant(fieldInfo),
                    fieldSetMethod,
                    boxedToLocal,
                    Expression.Convert(originalValue, typeof(object))
                ));
                continue;
            }

            Expression originalMemberValue = Expression.MakeMemberAccess(fromLocal, member);
            Expression clonedValueExpression;

            if (memberType.IsValueType())
            {
                MethodInfo structClone = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(memberType);
                clonedValueExpression = Expression.Call(structClone, originalMemberValue, state);
            }
            else
            {
                MethodInfo classDeep = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;
                MethodInfo classShallowTrack = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassShallowAndTrack))!;
                PropertyInfo useWorkListProp = FastCloneState.UseWorkListProp;
                Expression deepCall = Expression.Call(classDeep, originalMemberValue, state);
                Expression shallowCall = Expression.Call(classShallowTrack, originalMemberValue, state);
                Expression selected = Expression.Condition(Expression.Property(state, useWorkListProp), shallowCall, deepCall);
                clonedValueExpression = Expression.Convert(selected, memberType);
            }

            expressionList.Add(Expression.Call(
                Expression.Constant(fieldInfo),
                fieldSetMethod,
                boxedToLocal,
                Expression.Convert(clonedValueExpression, typeof(object))
            ));
        }
    }

    internal static void ForceSetField(FieldInfo field, object obj, object value)
    {
        field.SetValue(obj, value);
    }

#if true // MODERN
    internal readonly record struct ExpressionPosition(int Depth, int Index)
    {
        public ExpressionPosition Next() => this with { Index = Index + 1 };
        public ExpressionPosition Nested() => new ExpressionPosition(Depth + 1, 0);
    }
#else
    internal readonly struct ExpressionPosition : IEquatable<ExpressionPosition>
    {
        public int Depth { get; }
        public int Index { get; }

        public ExpressionPosition(int depth, int index)
        {
            Depth = depth;
            Index = index;
        }

        public ExpressionPosition Next() => new ExpressionPosition(Depth, Index + 1);
        public ExpressionPosition Nested() => new ExpressionPosition(Depth + 1, 0);

        public bool Equals(ExpressionPosition other)
        {
            return Depth == other.Depth && Index == other.Index;
        }

        public override bool Equals(object obj)
        {
            return obj is ExpressionPosition other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Depth * 397) ^ Index;
            }
        }

        public static bool operator ==(ExpressionPosition left, ExpressionPosition right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ExpressionPosition left, ExpressionPosition right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"ExpressionPosition {{ Depth = {Depth}, Index = {Index} }}";
        }
    }
#endif

    private static LabelTarget CreateLoopLabel(ExpressionPosition position)
    {
        string str = $"Loop_{position.Depth}_{position.Index}";
        return Expression.Label(str);
    }

    private static bool ShouldDeepCloneStructReadonlyFields(Type type)
    {
        if (type.Namespace == null)
        {
            return true;
        }

        // Some namespaces use structs as opaque handles to internal static state or singletons.
        // Clone cloning these handles breaks their identity (e.g. they no longer match the static singletons),
        // causing equality checks and lookups to fail.
        // For these specific namespaces, we assume structs with readonly fields are intended to be "Safe Handles"
        // and should have their readonly references preserved (shallow copied), not deep cloned.
        // We append "." to ensuring we match full namespace segments (e.g. "System.Net" matches "System.Net." but not "System.Network.")
        if (readonlyStructSafeHandleNamespaces.ContainsAnyPattern(type.Namespace + "."))
        {
            return false;
        }

        // Check for the user-defined safe handle attribute
        if (FastClonerCache.GetOrAddIsTypeSafeHandle(type, t => t.GetCustomAttribute<FastClonerSafeHandleAttribute>() != null))
        {
            return false;
        }

        return true;
    }

    internal static object? GenerateProcessMethod(Type realType, bool asObject) => GenerateProcessMethod(realType, asObject && realType.IsValueType(), new ExpressionPosition(0, 0));
    public static bool IsSetType(Type type) => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>));

    public static bool IsConcurrentBagOrQueue(Type type)
    {
        if (!type.IsGenericType)
            return false;

        if (typeof(ConcurrentBag<>).IsAssignableFrom(type))
            return true;

        Type open = type.GetGenericTypeDefinition();
        return open == typeof(ConcurrentBag<>) || open == typeof(ConcurrentQueue<>);
    }

    public static bool IsDictionaryType(Type type) => typeof(IDictionary).IsAssignableFrom(type) || type.GetInterfaces().Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private readonly struct ConstructorInfoEx
    {
        public ConstructorInfo Constructor { get; }
        public int ParameterCount { get; }
        public bool HasOptionalParameters { get; }

        public ConstructorInfoEx(ConstructorInfo constructor)
        {
            Constructor = constructor;
            ParameterInfo[] parameters = constructor.GetParameters();
            ParameterCount = parameters.Length;
            HasOptionalParameters = ParameterCount > 0 && parameters.All(p => p.HasDefaultValue);
        }
    }

    private static ConstructorInfoEx? FindCallableConstructor(Type type)
    {
        // take parameterless constructor
        ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
        return ctor != null ? new ConstructorInfoEx(ctor) : null;

        // using any other constructor that can be called without arguments increases chances we trigger side effects
        // we fall back to memberwise cloning instead
        // ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
        // return ctor != null ? new ConstructorInfoEx(ctor) : null;
    }
    
    /// <summary>
    /// Finds a constructor that takes an int capacity parameter.
    /// This is used to preallocate collections for better performance.
    /// </summary>
    private static ConstructorInfo? FindCapacityConstructor(Type type)
    {
        return type.GetConstructor([typeof(int)]);
    }
    

    private static NewExpression CreateNewExpressionWithCtor(ConstructorInfoEx ctorInfoEx)
    {
        if (ctorInfoEx.ParameterCount == 0)
        {
            return Expression.New(ctorInfoEx.Constructor);
        }

        // For constructors with optional parameters, create default values
        Expression[] arguments = ctorInfoEx.Constructor.GetParameters()
            .Select(p => Expression.Constant(p.HasDefaultValue ? p.DefaultValue : GetDefaultValue(p.ParameterType), p.ParameterType))
            .ToArray<Expression>();

        return Expression.New(ctorInfoEx.Constructor, arguments);
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static void AddMemberCloneExpressions(
        List<Expression> expressionList,
        ParameterExpression fromLocal,
        ParameterExpression toLocal,
        ParameterExpression state,
        Type type,
        bool skipReadonly = false)
    {
        ExpressionPosition currentPosition = new ExpressionPosition(0, 0);
        IEnumerable<MemberInfo> members = FastClonerCache.GetOrAddAllMembers(type, GetAllMembers);
        Dictionary<string, Type> ignoredEventDetails = FastClonerCache.GetOrAddIgnoredEventInfo(type, t =>
        {
            Dictionary<string, Type> details = new Dictionary<string, Type>();
            EventInfo[] events = t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (EventInfo evtInfo in events)
            {
                if (MemberIsIgnored(evtInfo))
                {
                    details[evtInfo.Name] = evtInfo.EventHandlerType;
                }
            }

            return details;
        });

        foreach (MemberInfo member in members)
        {
            Type memberType = member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => throw new ArgumentException($"Unsupported member type: {member.GetType()}")
            };

            bool canAssignDirect = member switch
            {
                FieldInfo fi => !fi.IsInitOnly,
                PropertyInfo pi => pi.CanWrite,
                _ => false
            };

            if (MemberIsIgnored(member))
            {
                if (canAssignDirect)
                {
                    expressionList.Add(Expression.Assign(
                        Expression.MakeMemberAccess(toLocal, member),
                        Expression.Default(memberType)
                    ));
                }

                continue;
            }

            if (member is FieldInfo fieldInfoForEventCheck && ignoredEventDetails.TryGetValue(fieldInfoForEventCheck.Name, out Type? evtType))
            {
                if (evtType == memberType)
                {
                    if (canAssignDirect)
                    {
                        expressionList.Add(Expression.Assign(
                            Expression.MakeMemberAccess(toLocal, member),
                            Expression.Default(memberType)
                        ));
                    }

                    continue;
                }
            }

            if (FastClonerCache.IsTypeIgnored(memberType))
            {
                if (canAssignDirect)
                {
                    expressionList.Add(Expression.Assign(
                        Expression.MakeMemberAccess(toLocal, member),
                        Expression.Default(memberType)
                    ));
                }

                continue;
            }

            if (member is PropertyInfo piLocal)
            {
                if (piLocal.CanWrite && MemberIsIgnored(piLocal))
                {
                    expressionList.Add(Expression.Assign(
                        Expression.Property(toLocal, piLocal),
                        Expression.Default(piLocal.PropertyType)
                    ));
                }

                continue;
            }

            if (!FastClonerSafeTypes.CanReturnSameObject(memberType))
            {
                bool shouldBeIgnored = false;

                if (MemberIsIgnored(member))
                {
                    shouldBeIgnored = true;
                }
                else if (member is FieldInfo fi)
                {
                    if (ignoredEventDetails.TryGetValue(fi.Name, out Type? eventHandlerTypeFromCache))
                    {
                        if (eventHandlerTypeFromCache == fi.FieldType)
                        {
                            shouldBeIgnored = true;
                        }
                    }
                }

                if (shouldBeIgnored)
                {
                    if (canAssignDirect)
                    {
                        expressionList.Add(Expression.Assign(
                            Expression.MakeMemberAccess(toLocal, member),
                            Expression.Default(memberType)
                        ));
                    }

                    continue;
                }

                // Check if member should copy reference directly (Shallow or Reference behavior)
                if (MemberShouldCopyReference(member))
                {
                    MemberExpression shallowSourceValue = Expression.MakeMemberAccess(fromLocal, member);
                    switch (member)
                    {
                        case FieldInfo fieldInfo:
                        {
                            bool isReadonly = readonlyFields.GetOrAdd(fieldInfo, f => f.IsInitOnly);
                            if (isReadonly)
                            {
                                if (!skipReadonly)
                                {
                                    expressionList.Add(Expression.Call(
                                        Expression.Constant(fieldInfo),
                                        fieldSetMethod,
                                        Expression.Convert(toLocal, typeof(object)),
                                        Expression.Convert(shallowSourceValue, typeof(object))));
                                }
                            }
                            else
                            {
                                expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), shallowSourceValue));
                            }
                            break;
                        }
                        case PropertyInfo { CanWrite: true }:
                        {
                            expressionList.Add(Expression.Assign(Expression.MakeMemberAccess(toLocal, member), shallowSourceValue));
                            break;
                        }
                    }
                    continue;
                }

                Expression clonedValueExpression;
                MemberExpression getMemberValue = Expression.MakeMemberAccess(fromLocal, member);
                Expression originalMemberValue = getMemberValue;

                if (memberType.IsValueType())
                {
                    MethodInfo structClone = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(memberType);
                    clonedValueExpression = Expression.Call(structClone, originalMemberValue, state);
                }
                else
                {
                    MethodInfo classDeep = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;
                    MethodInfo classShallowTrack = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassShallowAndTrack))!;
                    PropertyInfo useWorkListProp = FastCloneState.UseWorkListProp;
                    Expression deepCall = Expression.Call(classDeep, originalMemberValue, state);
                    Expression shallowCall = Expression.Call(classShallowTrack, originalMemberValue, state);
                    Expression selected = Expression.Condition(Expression.Property(state, useWorkListProp), shallowCall, deepCall);
                    clonedValueExpression = Expression.Convert(selected, memberType);
                }

                switch (member)
                {
                    case FieldInfo fieldInfo:
                    {
                        bool isReadonly = readonlyFields.GetOrAdd(fieldInfo, f => f.IsInitOnly);
                        if (isReadonly)
                        {
                            if (skipReadonly) break;
                            expressionList.Add(Expression.Call(
                                Expression.Constant(fieldInfo),
                                fieldSetMethod,
                                Expression.Convert(toLocal, typeof(object)),
                                Expression.Convert(clonedValueExpression, typeof(object))));
                        }
                        else
                        {
                            expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), clonedValueExpression));
                        }

                        break;
                    }
                    case PropertyInfo { CanWrite: true }:
                    {
                        expressionList.Add(Expression.Assign(Expression.MakeMemberAccess(toLocal, member), clonedValueExpression));
                        break;
                    }
                }

                currentPosition = currentPosition.Next();
            }
        }
    }

    private static object GenerateMemberwiseCloner(Type type, ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
        ParameterExpression toLocal = Expression.Variable(type);
        ParameterExpression fromLocal = Expression.Variable(type);
        List<Expression> expressionList = [];

        if (!type.IsValueType())
        {
            MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(Expression.Call(from, methodInfo), type)));
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, toLocal));

            AddMemberCloneExpressions(expressionList, fromLocal, toLocal, state, type, skipReadonly: false);
            expressionList.Add(Expression.Convert(toLocal, typeof(object)));
            List<ParameterExpression> blockParams = [fromLocal, toLocal];
            return Expression.Lambda<Func<object, FastCloneState, object>>(
                Expression.Block(blockParams, expressionList),
                from,
                state
            ).Compile();
        }

        expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(from, type)));
        expressionList.Add(Expression.Assign(fromLocal, toLocal));

        if (TypeHasReadonlyFields(type) && ShouldDeepCloneStructReadonlyFields(type))
        {
            AddMemberCloneExpressions(expressionList, fromLocal, toLocal, state, type, skipReadonly: true);

            ParameterExpression boxedToLocal = Expression.Variable(typeof(object));
            expressionList.Add(Expression.Assign(boxedToLocal, Expression.Convert(toLocal, typeof(object))));
            AddStructReadonlyFieldsCloneExpressions(expressionList, fromLocal, boxedToLocal, state, type);
            expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(boxedToLocal, type)));

            expressionList.Add(Expression.Convert(toLocal, typeof(object)));
            List<ParameterExpression> blockParams = [fromLocal, toLocal, boxedToLocal];
            return Expression.Lambda<Func<object, FastCloneState, object>>(
                Expression.Block(blockParams, expressionList),
                from,
                state
            ).Compile();
        }
        else
        {
            AddMemberCloneExpressions(expressionList, fromLocal, toLocal, state, type, skipReadonly: false);
            expressionList.Add(Expression.Convert(toLocal, typeof(object)));
            List<ParameterExpression> blockParams = [fromLocal, toLocal];
            return Expression.Lambda<Func<object, FastCloneState, object>>(
                Expression.Block(blockParams, expressionList),
                from,
                state
            ).Compile();
        }
    }

    private delegate object ProcessMethodDelegate(Type type, bool unboxStruct, ExpressionPosition position);

#if true // MODERN
    private static readonly FrozenDictionary<Type, ProcessMethodDelegate> knownTypeProcessors =
        new Dictionary<Type, ProcessMethodDelegate>
        {
            [typeof(ExpandoObject)] = (_, _, position) => GenerateExpandoObjectProcessor(position),
            [typeof(HttpRequestOptions)] = (_, _, position) => GenerateHttpRequestOptionsProcessor(position),
            [typeof(Array)] = (type, _, _) => GenerateProcessArrayMethod(type),
            [typeof(System.Text.Json.Nodes.JsonNode)] = (_, _, position) => GenerateJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonObject)] = (_, _, position) => GenerateJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonArray)] = (_, _, position) => GenerateJsonNodeProcessorModern(position),
            [typeof(System.Text.Json.Nodes.JsonValue)] = (_, _, position) => GenerateJsonNodeProcessorModern(position),
        }.ToFrozenDictionary();
#else
    private static readonly Dictionary<Type, ProcessMethodDelegate> knownTypeProcessors =
        new Dictionary<Type, ProcessMethodDelegate>
        {
            [typeof(ExpandoObject)] = (_, _, position) => GenerateExpandoObjectProcessor(position),
            [typeof(Array)] = (type, _, _) => GenerateProcessArrayMethod(type),
        };
#endif

    private static readonly AhoCorasick badTypes = new AhoCorasick([
        "Castle.Proxies.",
        "System.Data.Entity.DynamicProxies.",
        "NHibernate.Proxy."
    ]);

    private static readonly AhoCorasick readonlyStructSafeHandleNamespaces = new AhoCorasick([
        "System.Net.",
        "System.Reflection.",
        "System.IO.",
        "System.Runtime.",
        "System.Threading.",
        "System.Text.Json.",
        "System.Diagnostics."
    ]);

    private static readonly Dictionary<string, Func<Type, object?>> specialNamespaces = new Dictionary<string, Func<Type, object?>>
    {
        // these can be trusted to have their Clone() implemented properly
        { "System.Drawing", CloneIClonable },
        { "System.Globalization", CloneIClonable }
    };

    private static bool IsCloneable(Type type)
    {
        if (type.FullName is null)
        {
            return false;
        }

        return !badTypes.ContainsAnyPattern(type.FullName);
    }

    private static List<MemberInfo> GetAllMembers(Type type)
    {
        List<MemberInfo> members = [];
        Type? currentType = type;

        while (currentType != null && currentType != typeof(ContextBoundObject))
        {
            members.AddRange(currentType.GetDeclaredFields());
            members.AddRange(currentType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)); // Exclude indexers
            currentType = currentType.BaseType;
        }

        return members;
    }

    private static object? CloneIClonable(Type type)
    {
        if (typeof(ICloneable).IsAssignableFrom(type))
        {
            return (Func<object, FastCloneState, object>)((obj, state) =>
            {
                object result = ((ICloneable)obj).Clone();
                state.AddKnownRef(obj, result);
                return result;
            });
        }

        return null;
    }

    private static object? GenerateProcessMethod(Type type, bool unboxStruct, ExpressionPosition position)
    {
        if (!IsCloneable(type))
        {
            return null;
        }

        Type methodType = unboxStruct || type.IsClass() ? typeof(object) : type;

        if (FastClonerCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(methodType, "fromParam");
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState), "stateParam");
            Type funcGenericType = typeof(Func<,,>).MakeGenericType(methodType, typeof(FastCloneState), methodType);
            Expression resultExpression;

            if (type.IsValueType && !unboxStruct)
            {
                resultExpression = Expression.Default(type);
            }
            else
            {
                resultExpression = Expression.Constant(null, methodType);
            }

            return Expression.Lambda(funcGenericType, resultExpression, pFrom, pState).Compile();
        }

        if (knownTypeProcessors.TryGetValue(type, out ProcessMethodDelegate? handler))
        {
            return handler.Invoke(type, unboxStruct, position);
        }

        if (CustomTypeHandlers.TryGetValue(type, out Func<Type, bool, ExpressionPosition, object>? contribHandler))
        {
            return contribHandler.Invoke(type, unboxStruct, position);
        }

        if (type.Namespace is not null && specialNamespaces.TryGetValue(type.Namespace, out Func<Type, object?>? cloneMethod))
        {
            object? special = cloneMethod.Invoke(type);

            if (special is not null)
            {
                return special;
            }
        }

        if (IsDictionaryType(type))
        {
            return GenerateProcessDictionaryMethod(type, position);
        }

        if (IsConcurrentBagOrQueue(type))
        {
            return GenerateProcessConcurrentBagOrQueueMethod(type, position);
        }

        if (IsSetType(type))
        {
            return GenerateProcessSetMethod(type, position);
        }

        if (type.IsArray)
        {
            return GenerateProcessArrayMethod(type);
        }

        if (type.FullName is not null && type.FullName.StartsWith("System.Tuple`"))
        {
            Type[] genericArguments = type.GenericArguments();

            if (genericArguments.Length < 10 && genericArguments.All(FastClonerSafeTypes.CanReturnSameObject))
            {
                return GenerateProcessTupleMethod(type);
            }
        }

#if false // !MODERN
        if (type.FullName != null && type.FullName.StartsWith("System.Text.Json.Nodes."))
        {
             return GenerateJsonNodeProcessorNetstandard(type, position);
        }
#endif

        List<Expression> expressionList = [];
        ParameterExpression from = Expression.Parameter(methodType);
        ParameterExpression fromLocal = from;
        ParameterExpression toLocal = Expression.Variable(type);
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        if (!type.IsValueType())
        {
            MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(Expression.Call(from, methodInfo), type)));
            fromLocal = Expression.Variable(type);
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, toLocal));
        }
        else
        {
            if (unboxStruct)
            {
                expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(from, type)));
                fromLocal = Expression.Variable(type);
                expressionList.Add(Expression.Assign(fromLocal, toLocal));
            }
            else
            {
                expressionList.Add(Expression.Assign(toLocal, from));
            }
        }

        if (type.IsValueType && TypeHasReadonlyFields(type) && ShouldDeepCloneStructReadonlyFields(type))
        {
            AddMemberCloneExpressions(expressionList, fromLocal, toLocal, state, type, skipReadonly: true);

            ParameterExpression boxedToLocal = Expression.Variable(typeof(object));
            expressionList.Add(Expression.Assign(boxedToLocal, Expression.Convert(toLocal, typeof(object))));
            AddStructReadonlyFieldsCloneExpressions(expressionList, fromLocal, boxedToLocal, state, type);
            expressionList.Add(Expression.Assign(toLocal, Expression.Unbox(boxedToLocal, type)));

            expressionList.Add(Expression.Convert(toLocal, methodType));

            Type makeGenericType = typeof(Func<,,>).MakeGenericType(methodType, typeof(FastCloneState), methodType);
            List<ParameterExpression> blkParams = [];

            if (from != fromLocal)
            {
                blkParams.Add(fromLocal);
            }

            blkParams.Add(toLocal);
            blkParams.Add(boxedToLocal);

            return Expression.Lambda(makeGenericType, Expression.Block(blkParams, expressionList), from, state).Compile();
        }

        AddMemberCloneExpressions(expressionList, fromLocal, toLocal, state, type, skipReadonly: false);
        expressionList.Add(Expression.Convert(toLocal, methodType));

        Type funcType = typeof(Func<,,>).MakeGenericType(methodType, typeof(FastCloneState), methodType);
        List<ParameterExpression> blockParams = [];

        if (from != fromLocal)
        {
            blockParams.Add(fromLocal);
        }

        blockParams.Add(toLocal);

        return Expression.Lambda(funcType, Expression.Block(blockParams, expressionList), from, state).Compile();
    }

#if true // MODERN
    private static object GenerateHttpRequestOptionsProcessor(ExpressionPosition position)
    {
        if (FastClonerCache.IsTypeIgnored(typeof(HttpRequestOptions)))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState));
            return Expression.Lambda<Func<object, FastCloneState, object>>(pFrom, pFrom, pState).Compile();
        }

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
        ParameterExpression result = Expression.Variable(typeof(HttpRequestOptions));
        ParameterExpression tempMessage = Expression.Variable(typeof(HttpRequestMessage));
        ParameterExpression fromOptions = Expression.Variable(typeof(HttpRequestOptions));

        ConstructorInfo constructor = typeof(HttpRequestMessage).GetConstructor(Type.EmptyTypes)!;

        BlockExpression block = Expression.Block(
            [result, tempMessage, fromOptions],
            Expression.Assign(fromOptions, Expression.Convert(from, typeof(HttpRequestOptions))),
            Expression.Assign(tempMessage, Expression.New(constructor)),
            Expression.Assign(result, Expression.Property(tempMessage, "Options")),
            Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, result),
            Expression.Assign(result, Expression.Convert(
                Expression.Call(fromOptions, typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!),
                typeof(HttpRequestOptions)
            )),
            Expression.Call(tempMessage, typeof(IDisposable).GetMethod("Dispose")!),
            result
        );

        return Expression.Lambda<Func<object, FastCloneState, object>>(block, from, state).Compile();
    }
#endif

    private static object GenerateExpandoObjectProcessor(ExpressionPosition position)
    {
        return GenerateMemberwiseCloner(typeof(ExpandoObject), position);
    }

    private static object GenerateProcessDictionaryMethod(Type type, ExpressionPosition position)
    {
        Type[] genericArguments = type.GenericArguments();

        return genericArguments.Length switch
        {
            0 => GenerateNonGenericDictionaryProcessor(type, position),
            1 => HandleSingleGenericArgument(type, genericArguments[0], position),
            2 => GenerateDictionaryProcessor(type, genericArguments[0], genericArguments[1], true, position),
            _ => throw new ArgumentException($"Unexpected number of generic arguments: {genericArguments.Length}")
        };
    }

    private static object GenerateNonGenericDictionaryProcessor(Type type, ExpressionPosition position)
    {
        Type? dictionaryInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                  i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryInterface is not null)
        {
            Type[] interfaceArgs = dictionaryInterface.GetGenericArguments();
            return GenerateDictionaryProcessor(type, interfaceArgs[0], interfaceArgs[1], true, position);
        }

        return GenerateDictionaryProcessor(type, typeof(object), typeof(object), false, position);
    }

    private static object HandleSingleGenericArgument(Type type, Type genericArg, ExpressionPosition position)
    {
        if (genericArg.IsGenericType && genericArg.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            Type[] kvpArguments = genericArg.GetGenericArguments();
            return GenerateDictionaryProcessor(type, kvpArguments[0], kvpArguments[1], true, position);
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return GenerateDictionaryProcessor(type, typeof(object), genericArg, true, position);
        }

        throw new ArgumentException($"Unsupported dictionary type with single generic argument: {type.FullName}");
    }

    private static object GenerateDictionaryProcessor(Type dictType, Type keyType, Type valueType, bool isGeneric, ExpressionPosition position)
    {
        bool isImmutable = IsImmutableCollection(dictType);
        
        if (!isImmutable && FastClonerSafeTypes.HasStableHashSemantics(keyType) && !FastClonerCache.IsTypeIgnored(keyType) && !FastClonerCache.IsTypeIgnored(valueType))
        {
            return GenerateMemberwiseCloner(dictType, position);
        }
        
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
        LabelTarget returnNullLabel = Expression.Label(typeof(object));
        ConditionalExpression nullCheck = Expression.IfThen(
            Expression.Equal(from, Expression.Constant(null)),
            Expression.Return(returnNullLabel, Expression.Constant(null))
        );

        if (isImmutable)
        {
            return GenerateImmutableDictionaryProcessor(dictType, keyType, valueType, from, state, returnNullLabel, nullCheck);
        }

        // read-only
#if true // MODERN
        bool isReadOnly = dictType.Name.Contains("ReadOnly", StringComparison.InvariantCultureIgnoreCase) ||
                          (dictType.IsGenericType && dictType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>));
#else
        bool isReadOnly = dictType.Name.IndexOf("ReadOnly", StringComparison.InvariantCultureIgnoreCase) >= 0 || 
                          (dictType.IsGenericType && dictType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>));
#endif

        Type innerDictType = isReadOnly
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : dictType;

        // Check constructors
        ConstructorInfoEx? ctorInfo = isReadOnly
            ? dictType.GetConstructor([innerDictType]) is { } ctor ? new ConstructorInfoEx(ctor) : null
            : FindCallableConstructor(dictType);

        // If we can't find appropriate constructor, fall back to memberwise cloning
        if (ctorInfo is null)
        {
            return GenerateMemberwiseCloner(dictType, position);
        }

        ParameterExpression result = Expression.Variable(dictType);
        ParameterExpression innerDict = isReadOnly
            ? Expression.Variable(innerDictType)
            : result;
        
        // Create a typed reference to get Count for capacity preallocation
        ParameterExpression typedFrom = Expression.Variable(dictType);
        BinaryExpression assignTypedFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, dictType)
        );

        // Create instance of inner dictionary with capacity preallocation
        ConstructorInfo? capacityCtor = FindCapacityConstructor(innerDictType);
        ConstructorInfoEx? innerDictCtorInfo = FindCallableConstructor(innerDictType);

        if (innerDictCtorInfo is null && capacityCtor is null)
        {
            return GenerateMemberwiseCloner(dictType, position);
        }
        
        Expression createInnerDict;
        if (capacityCtor is not null)
        {
            // Use capacity constructor with Count from source dictionary
            PropertyInfo? countProperty = dictType.GetProperty("Count");
            if (countProperty is not null)
            {
                Expression countExpr = Expression.Property(typedFrom, countProperty);
                createInnerDict = Expression.Assign(
                    innerDict,
                    Expression.New(capacityCtor, countExpr)
                );
            }
            else
            {
                // Fall back to parameterless constructor if Count property not found
                createInnerDict = Expression.Assign(
                    innerDict,
                    CreateNewExpressionWithCtor(innerDictCtorInfo!.Value)
                );
            }
        }
        else
        {
            createInnerDict = Expression.Assign(
                innerDict,
                CreateNewExpressionWithCtor(innerDictCtorInfo!.Value)
            );
        }

        // If ReadOnlyDictionary, use inner Dictionary
        Expression createResult = isReadOnly
            ? Expression.Assign(
                result,
                Expression.New(
                    dictType.GetConstructor([innerDictType])!,
                    innerDict
                )
            )
            : createInnerDict;

        // Add reference to state for cycle detection
        Expression addRef = Expression.Call(
            state,
            StaticMethodInfos.DeepCloneStateMethods.AddKnownRef,
            from,
            result
        );

        Type methodSourceType = isReadOnly ? innerDictType : dictType;
        MethodInfo? addMethod = methodSourceType.GetMethod("Add", [keyType, valueType])
                                ?? methodSourceType.GetMethods()
                                    .FirstOrDefault(m => m.Name == "TryAdd" &&
                                                         m.GetParameters().Length is 2 &&
                                                         m.GetParameters()[0].ParameterType == keyType &&
                                                         m.GetParameters()[1].ParameterType == valueType);

        if (addMethod is null)
        {
            return GenerateMemberwiseCloner(dictType, position);
        }

        // Setup enumerator
        Type enumeratorType = isGeneric
            ? typeof(IEnumerator<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType))
            : typeof(IDictionaryEnumerator);

        ParameterExpression enumerator = Expression.Variable(enumeratorType);

        // Get clone methods
        MethodInfo keyCloneMethod = keyType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(keyType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        MethodInfo valueCloneMethod = valueType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(valueType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        BlockExpression iterationBlock = isGeneric
            ? GenerateGenericDictionaryIteration(enumerator, keyType, valueType, keyCloneMethod, valueCloneMethod, innerDict, addMethod, state, position)
            : GenerateNonGenericDictionaryIteration(enumerator, keyCloneMethod, valueCloneMethod, innerDict, addMethod, state, position);

        Type enumerableType = isGeneric
            ? typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType))
            : typeof(IDictionary);

        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");

        if (getEnumeratorMethod is null)
        {
            throw new InvalidOperationException($"Cannot find GetEnumerator method for type {enumerableType.FullName}");
        }

        Expression getEnumerator = Expression.Assign(
            enumerator,
            Expression.Convert(
                Expression.Call(
                    Expression.Convert(from, enumerableType),
                    getEnumeratorMethod
                ),
                enumeratorType
            )
        );

        // Combine into final expression
        List<ParameterExpression> blockVars = isReadOnly 
            ? [result, innerDict, enumerator, typedFrom] 
            : [result, enumerator, typedFrom];
        
        BlockExpression block = Expression.Block(
            blockVars,
            nullCheck,
            assignTypedFrom,
            createInnerDict,
            createResult,
            addRef,
            getEnumerator,
            iterationBlock,
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );

        return Expression.Lambda<Func<object, FastCloneState, object>>(
            block,
            from,
            state
        ).Compile();
    }

    private static object GenerateImmutableDictionaryProcessor(Type dictType, Type keyType, Type valueType, ParameterExpression from, ParameterExpression state, LabelTarget returnNullLabel, Expression nullCheck)
    {
        ParameterExpression typedFrom = Expression.Variable(dictType);
        ParameterExpression result = Expression.Variable(dictType);
        BinaryExpression castFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, dictType)
        );

        MethodInfo? addMethod = dictType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.ReturnType == dictType &&
                                 m.GetParameters().Length == 2 &&
                                 m.GetParameters()[0].ParameterType == keyType &&
                                 m.GetParameters()[1].ParameterType == valueType);

        if (addMethod == null)
        {
            return Expression.Lambda<Func<object, FastCloneState, object>>(
                from,
                from,
                state
            ).Compile();
        }

        MethodInfo? emptyMethod = dictType.GetMethod("Empty", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        MethodInfo? createMethod = dictType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

        // try to make an empty copy
        Expression createEmpty;
        if (emptyMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, emptyMethod)
            );
        }
        else if (createMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, createMethod)
            );
        }
        else
        {
            MethodInfo? clearMethod = dictType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);

            if (clearMethod != null && clearMethod.ReturnType == dictType)
            {
                createEmpty = Expression.Assign(
                    result,
                    Expression.Call(typedFrom, clearMethod)
                );
            }
            else
            {
                // bail
                return Expression.Lambda<Func<object, FastCloneState, object>>(
                    from,
                    from,
                    state
                ).Compile();
            }
        }

        MethodInfo keyCloneMethod = keyType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(keyType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        MethodInfo valueCloneMethod = valueType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(valueType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(
            typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));
        ParameterExpression enumerator = Expression.Variable(enumeratorType);

        Type enumerableType = typeof(IEnumerable<>).MakeGenericType(
            typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));
        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");

        BinaryExpression assignEnumerator = Expression.Assign(
            enumerator,
            Expression.Call(
                Expression.Convert(typedFrom, enumerableType),
                getEnumeratorMethod
            )
        );

        // iterate over pairs
        PropertyInfo? current = enumeratorType.GetProperty("Current");
        Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
        ParameterExpression kvp = Expression.Variable(kvpType);
        ParameterExpression key = Expression.Variable(keyType);
        ParameterExpression value = Expression.Variable(valueType);

        LabelTarget breakLabel = Expression.Label("LoopBreak");

        Expression originalKeyProperty = Expression.Property(kvp, "Key");
        Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyProperty, state);

        if (!keyType.IsValueType())
        {
            clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
        }

        Expression keyForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
            originalKeyProperty,
            clonedKeyCall
        );


        Expression originalValueProperty = Expression.Property(kvp, "Value");
        Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueProperty, state);

        if (!valueType.IsValueType())
        {
            clonedValueCall = Expression.Convert(clonedValueCall, valueType);
        }

        Expression valueForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
            originalValueProperty,
            clonedValueCall
        );

        BlockExpression loopBody = Expression.Block(
            [kvp, key, value],
            Expression.Assign(kvp, Expression.Property(enumerator, current)),
            Expression.Assign(
                key,
                keyForAdd
            ),
            Expression.Assign(
                value,
                valueForAdd
            ),
            Expression.Assign(
                result,
                Expression.Call(result, addMethod, key, value)
            )
        );

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod("MoveNext")),
                loopBody,
                Expression.Break(breakLabel)
            ),
            breakLabel
        );

        // detect cycles
        MethodCallExpression addRef = Expression.Call(
            state,
            StaticMethodInfos.DeepCloneStateMethods.AddKnownRef,
            from,
            result
        );

        BlockExpression block = Expression.Block(
            [typedFrom, result, enumerator],
            nullCheck,
            castFrom,
            createEmpty,
            assignEnumerator,
            loop,
            addRef,
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );

        return Expression.Lambda<Func<object, FastCloneState, object>>(
            block,
            from,
            state
        ).Compile();
    }

    /// <summary>
    /// Note: this is based on a few heuristics, "best effort".
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private static bool IsImmutableCollection(Type type)
    {
        if (type.Namespace == "System.Collections.Immutable")
        {
            return true;
        }

        if (type.GetInterfaces().Any(x => x.Namespace == "System.Collections.Immutable"))
        {
            return true;
        }

        Attribute? immutableAttr = type.GetCustomAttributes().FirstOrDefault(attr => attr.GetType().Name.Contains("Immutable"));
        return immutableAttr is not null || type.Name.Contains("Immutable");
    }


    private static BlockExpression GenerateGenericDictionaryIteration(ParameterExpression enumerator, Type keyType, Type valueType, MethodInfo keyCloneMethod, MethodInfo valueCloneMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        PropertyInfo current = enumerator.Type.GetProperty(nameof(IEnumerator<object>.Current))!;
        LabelTarget breakLabel = CreateLoopLabel(position);
        Type dictionaryType = local.Type;
        bool isSingleGenericParameter = dictionaryType.GetGenericArguments().Length is 1;

        if (isSingleGenericParameter)
        {
            Type singleGenericType = dictionaryType.GetGenericArguments()[0];

            if (singleGenericType.IsGenericType && singleGenericType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                Type[] kvpTypes = singleGenericType.GetGenericArguments();
                ParameterExpression kvp = Expression.Variable(singleGenericType);
                BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));

                Expression originalSingleKey = Expression.Property(kvp, "Key");
                Expression clonedSingleKeyCall = Expression.Call(keyCloneMethod, originalSingleKey, state);

                if (!kvpTypes[0].IsValueType())
                {
                    clonedSingleKeyCall = Expression.Convert(clonedSingleKeyCall, kvpTypes[0]);
                }

                Expression keyForNewKvp = Expression.Condition(
                    Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(kvpTypes[0], typeof(Type))),
                    originalSingleKey,
                    clonedSingleKeyCall
                );

                Expression originalSingleValue = Expression.Property(kvp, "Value");
                Expression clonedSingleValueCall = Expression.Call(valueCloneMethod, originalSingleValue, state);
                if (!kvpTypes[1].IsValueType()) clonedSingleValueCall = Expression.Convert(clonedSingleValueCall, kvpTypes[1]);

                Expression valueForNewKvp = Expression.Condition(
                    Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(kvpTypes[1], typeof(Type))),
                    originalSingleValue,
                    clonedSingleValueCall
                );

                NewExpression newKvp = Expression.New(
                    singleGenericType.GetConstructor([kvpTypes[0], kvpTypes[1]])!,
                    Expression.Convert(keyForNewKvp, kvpTypes[0]),
                    Expression.Convert(valueForNewKvp, kvpTypes[1])
                );
                MethodInfo collectionAddMethod = dictionaryType.GetMethod("Add", [singleGenericType])!;
                MethodCallExpression addKvp = Expression.Call(local, collectionAddMethod, newKvp);

                LoopExpression loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                        Expression.Block([kvp], assignKvp, addKvp),
                        Expression.Break(breakLabel)),
                    breakLabel);

                return Expression.Block(loop);
            }
        }

        {
            Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
            ParameterExpression kvp = Expression.Variable(kvpType);
            ParameterExpression key = Expression.Variable(keyType);
            ParameterExpression value = Expression.Variable(valueType);

            BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));

            Expression originalKeyProperty = Expression.Property(kvp, "Key");
            Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyProperty, state);

            if (!keyType.IsValueType())
            {
                clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
            }

            Expression keyToAssign = Expression.Condition(
                Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
                originalKeyProperty,
                clonedKeyCall
            );

            Expression originalValueProperty = Expression.Property(kvp, "Value");
            Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueProperty, state);

            if (!valueType.IsValueType())
            {
                clonedValueCall = Expression.Convert(clonedValueCall, valueType);
            }

            Expression valueToAssign = Expression.Condition(
                Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
                valueType.IsValueType
                    ? Expression.Default(valueType)
                    : Expression.Constant(null, valueType),
                clonedValueCall
            );

            BinaryExpression assignKey = Expression.Assign(key, keyToAssign);
            BinaryExpression assignValue = Expression.Assign(value, valueToAssign);
            MethodCallExpression addKvp = Expression.Call(local, addMethod, key, value);

            LoopExpression loop = Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                    Expression.Block([kvp, key, value],
                        assignKvp,
                        assignKey,
                        assignValue,
                        addKvp),
                    Expression.Break(breakLabel)),
                breakLabel);

            return Expression.Block(loop);
        }
    }

    private static BlockExpression GenerateNonGenericDictionaryIteration(ParameterExpression enumerator, MethodInfo keyCloneMethod, MethodInfo valueCloneMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        MemberExpression current = Expression.Property(enumerator, nameof(IDictionaryEnumerator.Entry));
        ParameterExpression key = Expression.Variable(typeof(object));
        ParameterExpression value = Expression.Variable(typeof(object));

        Expression originalKeyObject = Expression.Property(current, "Key");
        Expression clonedKeyCall = Expression.Call(keyCloneMethod, originalKeyObject, state);

        Expression keyRuntimeType = Expression.Call(originalKeyObject, typeof(object).GetMethod(nameof(GetType))!);
        Expression isKeyIgnored = Expression.Call(null, IsTypeIgnoredMethodInfo, keyRuntimeType);

        Expression keyToAssign = Expression.Condition(
            Expression.OrElse(
                Expression.Equal(originalKeyObject, Expression.Constant(null, typeof(object))),
                isKeyIgnored
            ),
            originalKeyObject,
            clonedKeyCall
        );

        Expression originalValueObject = Expression.Property(current, "Value");
        Expression clonedValueCall = Expression.Call(valueCloneMethod, originalValueObject, state);

        Expression valueRuntimeType = Expression.Call(originalValueObject, typeof(object).GetMethod(nameof(GetType))!);
        Expression isValueIgnored = Expression.Call(null, IsTypeIgnoredMethodInfo, valueRuntimeType);

        Expression valueToAssign = Expression.Condition(
            Expression.OrElse(
                Expression.Equal(originalValueObject, Expression.Constant(null, typeof(object))),
                isValueIgnored
            ),
            originalValueObject,
            clonedValueCall
        );

        BinaryExpression assignKey = Expression.Assign(key, keyToAssign);
        BinaryExpression assignValue = Expression.Assign(value, valueToAssign);
        MethodCallExpression addEntry = Expression.Call(local, addMethod, key, value);

        LabelTarget breakLabel = CreateLoopLabel(position);

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!),
                Expression.Block(
                    [key, value],
                    assignKey,
                    assignValue,
                    addEntry),
                Expression.Break(breakLabel)),
            breakLabel);

        return Expression.Block(loop);
    }

    private static object GenerateProcessConcurrentBagOrQueueMethod(Type type, ExpressionPosition position)
    {
        if (FastClonerCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState));
            return Expression.Lambda<Func<object, FastCloneState, object>>(pFrom, pFrom, pState).Compile();
        }

        Type elementType = type.GetGenericArguments()[0];

        MethodInfo cloneElementMethod = elementType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(elementType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        ParameterExpression local = Expression.Variable(type);

        // Constructor
        BinaryExpression assign = Expression.Assign(local, Expression.New(type.GetConstructor(Type.EmptyTypes)!));

        // Add Method
        MethodInfo? addMethod = type.GetMethod("Add", [elementType]) ?? type.GetMethod("Enqueue", [elementType]);

        if (addMethod == null) return GenerateMemberwiseCloner(type, position);

        // Foreach
        BlockExpression foreachBlock = GenerateForeachBlock(
            from, elementType, null, cloneElementMethod, null, local, addMethod, state, position
        );

        Type funcType = typeof(Func<object, FastCloneState, object>);

        return Expression.Lambda(
            funcType,
            Expression.Block(
                [local],
                assign,
                Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, local),
                foreachBlock,
                local
            ),
            from,
            state
        ).Compile();
    }

    private static object GenerateProcessSetMethod(Type type, ExpressionPosition position)
    {
        if (FastClonerCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState));
            return Expression.Lambda<Func<object, FastCloneState, object>>(pFrom, pFrom, pState).Compile();
        }

        Type elementType = type.GenericArguments()[0];
        
        // Fast path check first - avoid creating expressions if we don't need them
        bool isImmutable = IsImmutableCollection(type);
        
        if (!isImmutable && FastClonerSafeTypes.HasStableHashSemantics(elementType) && !FastClonerCache.IsTypeIgnored(elementType))
        {
            return GenerateMemberwiseCloner(type, position);
        }

        // Now create expressions for immutable or slow path
        MethodInfo cloneElementMethod = elementType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(elementType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        if (isImmutable)
        {
            return GenerateImmutableSetProcessor(type, elementType, from, state, position);
        }

        ParameterExpression local = Expression.Variable(type);

#if true // MODERN
        bool isReadOnly = type.Name.Contains("ReadOnly", StringComparison.InvariantCultureIgnoreCase);
#else
        bool isReadOnly = type.Name.IndexOf("ReadOnly", StringComparison.InvariantCultureIgnoreCase) >= 0;
#endif

        // Use HashSet as inner collection
        Type innerSetType = isReadOnly
            ? typeof(HashSet<>).MakeGenericType(elementType)
            : type;

        ParameterExpression innerSet = isReadOnly
            ? Expression.Variable(innerSetType)
            : local;
        
        // Create a typed reference to get Count for capacity preallocation
        ParameterExpression typedFrom = Expression.Variable(type);
        BinaryExpression assignTypedFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, type)
        );

        // Try to use capacity constructor for better performance
        ConstructorInfo? capacityCtor = FindCapacityConstructor(innerSetType);
        ConstructorInfo? parameterlessCtor = innerSetType.GetConstructor(Type.EmptyTypes);
        
        if (capacityCtor is null && parameterlessCtor is null)
        {
            return GenerateMemberwiseCloner(type, position);
        }
        
        Expression assignInnerSet;
        if (capacityCtor is not null)
        {
            // Use capacity constructor with Count from source set
            PropertyInfo? countProperty = type.GetProperty("Count");
            if (countProperty is not null)
            {
                Expression countExpr = Expression.Property(typedFrom, countProperty);
                assignInnerSet = Expression.Assign(
                    innerSet,
                    Expression.New(capacityCtor, countExpr)
                );
            }
            else
            {
                // Fall back to parameterless constructor if Count property not found
                assignInnerSet = Expression.Assign(
                    innerSet,
                    Expression.New(parameterlessCtor!)
                );
            }
        }
        else
        {
            assignInnerSet = Expression.Assign(
                innerSet,
                Expression.New(parameterlessCtor!)
            );
        }

        // Get Add method from inner set
        MethodInfo? addMethod = innerSetType.GetMethod("Add", [elementType]) ??
                                typeof(ISet<>).MakeGenericType(elementType).GetMethod("Add") ??
                                innerSetType.GetMethod("Add") ??
                                innerSetType.GetMethod("TryAdd");

        if (addMethod == null)
        {
            return GenerateMemberwiseCloner(type, position);
        }

        // Generate foreach block using inner set
        BlockExpression foreachBlock = GenerateForeachBlock(
            from,
            elementType,
            null,
            cloneElementMethod,
            null,
            innerSet,
            addMethod,
            state,
            position
        );

        Expression createMutableColl = assignInnerSet;
        Expression createFinalCollShell = isReadOnly ? Expression.Assign(local, Expression.New(type.GetConstructor([innerSetType])!, innerSet)) : Expression.Empty();

        Type funcType = typeof(Func<object, FastCloneState, object>);
        
        List<ParameterExpression> setBlockVars = isReadOnly 
            ? [local, innerSet, typedFrom] 
            : [local, typedFrom];

        return Expression.Lambda(
            funcType,
            Expression.Block(
                setBlockVars,
                assignTypedFrom,
                createMutableColl,
                createFinalCollShell,
                Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, local),
                foreachBlock,
                local
            ),
            from,
            state
        ).Compile();
    }

    private static object GenerateImmutableSetProcessor(Type setType, Type elementType, ParameterExpression from, ParameterExpression state, ExpressionPosition position)
    {
        ParameterExpression typedFrom = Expression.Variable(setType);
        ParameterExpression result = Expression.Variable(setType);
        LabelTarget returnNullLabel = Expression.Label(typeof(object));

        Expression nullCheck = Expression.IfThen(
            Expression.Equal(from, Expression.Constant(null)),
            Expression.Return(returnNullLabel, Expression.Constant(null))
        );

        BinaryExpression castFrom = Expression.Assign(
            typedFrom,
            Expression.Convert(from, setType)
        );

        MethodInfo? addMethod = setType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Add" && m.ReturnType == setType &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == elementType);

        if (addMethod == null)
        {
            addMethod = setType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Union" && m.ReturnType == setType);

            if (addMethod == null)
            {
                return GenerateMemberwiseCloner(setType, position);
            }
        }

        MethodInfo? emptyMethod = setType.GetMethod("Empty", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        MethodInfo? createMethod = setType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        Expression createEmpty;

        if (emptyMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, emptyMethod)
            );
        }
        else if (createMethod != null)
        {
            createEmpty = Expression.Assign(
                result,
                Expression.Call(null, createMethod)
            );
        }
        else
        {
            MethodInfo? clearMethod = setType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);

            if (clearMethod != null && clearMethod.ReturnType == setType)
            {
                createEmpty = Expression.Assign(
                    result,
                    Expression.Call(typedFrom, clearMethod)
                );
            }
            else
            {
                return Expression.Lambda<Func<object, FastCloneState, object>>(
                    from,
                    from,
                    state
                ).Compile();
            }
        }

        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(elementType);
        ParameterExpression enumerator = Expression.Variable(enumeratorType);

        Type enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        MethodInfo? getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");

        BinaryExpression assignEnumerator = Expression.Assign(
            enumerator,
            Expression.Call(
                Expression.Convert(typedFrom, enumerableType),
                getEnumeratorMethod
            )
        );

        PropertyInfo? current = enumeratorType.GetProperty("Current");
        ParameterExpression element = Expression.Variable(elementType);

        LabelTarget breakLabel = Expression.Label("LoopBreak");

        MethodInfo elementCloneMethod = elementType.IsValueType()
            ? typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneStructInternal))!.MakeGenericMethod(elementType)
            : typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneClassInternal))!;

        Expression originalElementProperty = Expression.Property(enumerator, current!);
        Expression clonedElementCall = Expression.Call(elementCloneMethod, originalElementProperty, state);

        if (!elementType.IsValueType())
        {
            clonedElementCall = Expression.Convert(clonedElementCall, elementType);
        }

        Expression elementForAdd = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(elementType, typeof(Type))),
            elementType.IsValueType
                ? Expression.Default(elementType)
                : Expression.Constant(null, elementType),
            clonedElementCall
        );

        BlockExpression loopBody = Expression.Block(
            [element],
            Expression.Assign(element, elementForAdd),
            Expression.Assign(
                result,
                Expression.Call(result, addMethod, element)
            )
        );

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, typeof(IEnumerator).GetMethod("MoveNext")!),
                loopBody,
                Expression.Break(breakLabel)
            ),
            breakLabel
        );

        BlockExpression block = Expression.Block(
            [typedFrom, result, enumerator],
            nullCheck,
            castFrom,
            createEmpty,
            Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, result),
            assignEnumerator,
            loop,
            Expression.Label(returnNullLabel, Expression.Convert(result, typeof(object)))
        );

        return Expression.Lambda<Func<object, FastCloneState, object>>(
            block,
            from,
            state
        ).Compile();
    }

    private static object GenerateProcessArrayMethod(Type type)
    {
        if (FastClonerCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState));
            Type ft = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(FastCloneState), typeof(object));
            return Expression.Lambda(ft, pFrom, pFrom, pState).Compile();
        }

        Type? elementType = type.GetElementType();
        int rank = type.GetArrayRank();

        MethodInfo methodInfo;

        // multidim or not zero-based arrays
        if (rank != 1 || type != elementType?.MakeArrayType())
        {
            if (rank == 2 && type == elementType?.MakeArrayType(2))
            {
                // small optimization for 2 dim arrays
                methodInfo = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.Clone2DimArrayInternal))!.MakeGenericMethod(elementType);
            }
            else
            {
                methodInfo = typeof(FastClonerGenerator).GetPrivateStaticMethod(nameof(FastClonerGenerator.CloneAbstractArrayInternal))!;
            }
        }
        else
        {
            string methodName;

            if (FastClonerCache.IsTypeIgnored(elementType))
            {
                methodName = elementType.IsValueType ? nameof(FastClonerGenerator.Clone1DimArrayStructInternal) : nameof(FastClonerGenerator.Clone1DimArrayClassInternal);
            }
            else if (FastClonerSafeTypes.CanReturnSameObject(elementType))
            {
                methodName = nameof(FastClonerGenerator.Clone1DimArraySafeInternal);
            }
            else if (elementType.IsValueType())
            {
                methodName = nameof(FastClonerGenerator.Clone1DimArrayStructInternal);
            }
            else
            {
                methodName = nameof(FastClonerGenerator.Clone1DimArrayClassInternal);
            }

            methodInfo = typeof(FastClonerGenerator).GetPrivateStaticMethod(methodName)!.MakeGenericMethod(elementType);
        }

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
        MethodCallExpression call = Expression.Call(methodInfo, Expression.Convert(from, type), state);

        Type funcType = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(FastCloneState), typeof(object));

        return Expression.Lambda(funcType, call, from, state).Compile();
    }

    private static BlockExpression GenerateForeachBlock(ParameterExpression from, Type keyType, Type? valueType, MethodInfo cloneKeyMethod, MethodInfo? cloneValueMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state, ExpressionPosition position)
    {
        Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(valueType == null ? keyType : typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType));

        ParameterExpression enumerator = Expression.Variable(enumeratorType);
        MethodInfo moveNext = typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!;
        PropertyInfo current = enumeratorType.GetProperty(nameof(IEnumerator.Current))!;

        MethodInfo getEnumerator = typeof(IEnumerable).GetMethod(nameof(IEnumerable.GetEnumerator))!;

        LabelTarget breakLabel = CreateLoopLabel(position);

        LoopExpression loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumerator, moveNext),
                Expression.Block(
                    valueType is null
                        ? GenerateSetAddBlock(enumerator, current, keyType, cloneKeyMethod, local, addMethod, state)
                        : GenerateDictionaryAddBlock(enumerator, current, keyType, valueType, cloneKeyMethod, cloneValueMethod!, local, addMethod, state)),
                Expression.Break(breakLabel)),
            breakLabel);

        return Expression.Block(
            [enumerator],
            Expression.Assign(
                enumerator,
                Expression.Convert(
                    Expression.Call(Expression.Convert(from, typeof(IEnumerable)), getEnumerator),
                    enumeratorType)),
            loop);
    }

    private static BlockExpression GenerateSetAddBlock(ParameterExpression enumerator, PropertyInfo current, Type elementType, MethodInfo cloneElementMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state)
    {
        ParameterExpression elementVar = Expression.Variable(elementType);
        Expression originalElement = Expression.Property(enumerator, current);
        Expression clonedElementCall = Expression.Call(cloneElementMethod, originalElement, state);

        if (!elementType.IsValueType())
        {
            clonedElementCall = Expression.Convert(clonedElementCall, elementType);
        }

        Expression elementToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(elementType, typeof(Type))),
            originalElement,
            clonedElementCall
        );

        BinaryExpression assignElement = Expression.Assign(elementVar, elementToAssign);
        MethodCallExpression addElement = Expression.Call(local, addMethod, elementVar);

        return Expression.Block([elementVar], assignElement, addElement);
    }

    private static BlockExpression GenerateDictionaryAddBlock(ParameterExpression enumerator, PropertyInfo current, Type keyType, Type valueType, MethodInfo cloneKeyMethod, MethodInfo cloneValueMethod, ParameterExpression local, MethodInfo addMethod, ParameterExpression state)
    {
        Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
        ParameterExpression kvp = Expression.Variable(kvpType);
        BinaryExpression assignKvp = Expression.Assign(kvp, Expression.Property(enumerator, current));

        ParameterExpression keyVar = Expression.Variable(keyType);
        ParameterExpression valueVar = Expression.Variable(valueType);

        Expression originalKeyProperty = Expression.Property(kvp, "Key");
        Expression clonedKeyCall = Expression.Call(cloneKeyMethod, originalKeyProperty, state);

        if (!keyType.IsValueType())
        {
            clonedKeyCall = Expression.Convert(clonedKeyCall, keyType);
        }

        Expression keyToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(keyType, typeof(Type))),
            originalKeyProperty,
            clonedKeyCall
        );

        Expression originalValueProperty = Expression.Property(kvp, "Value");
        Expression clonedValueCall = Expression.Call(cloneValueMethod, originalValueProperty, state);

        if (!valueType.IsValueType())
        {
            clonedValueCall = Expression.Convert(clonedValueCall, valueType);
        }

        Expression valueToAssign = Expression.Condition(
            Expression.Call(null, IsTypeIgnoredMethodInfo, Expression.Constant(valueType, typeof(Type))),
            originalValueProperty,
            clonedValueCall
        );

        BinaryExpression assignKey = Expression.Assign(keyVar, keyToAssign);
        BinaryExpression assignValue = Expression.Assign(valueVar, valueToAssign);

        MethodCallExpression addKvp = Expression.Call(local, addMethod, keyVar, valueVar);

        return Expression.Block([kvp, keyVar, valueVar], assignKvp, assignKey, assignValue, addKvp);
    }

    private static object GenerateProcessTupleMethod(Type type)
    {
        if (FastClonerCache.IsTypeIgnored(type))
        {
            ParameterExpression pFrom = Expression.Parameter(typeof(object));
            ParameterExpression pState = Expression.Parameter(typeof(FastCloneState));
            Type ft = typeof(Func<,,>).MakeGenericType(typeof(object), typeof(FastCloneState), typeof(object));
            return Expression.Lambda(ft, pFrom, pFrom, pState).Compile();
        }

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        ParameterExpression local = Expression.Variable(type);
        BinaryExpression assign = Expression.Assign(local, Expression.Convert(from, type));

        Type funcType = typeof(Func<object, FastCloneState, object>);

        int tupleLength = type.GenericArguments().Length;

        BinaryExpression constructor = Expression.Assign(
            local,
            Expression.New(type.GetPublicConstructors().First(x => x.GetParameters().Length == tupleLength),
                type.GetPublicProperties().OrderBy(x => x.Name)
                    .Where(x => x.CanRead && x.Name.StartsWith("Item") && char.IsDigit(x.Name[4]))
                    .Select(x => Expression.Property(local, x.Name))));

        return Expression.Lambda(
            funcType,
            Expression.Block(
                [local],
                assign,
                constructor,
                Expression.Call(state, StaticMethodInfos.DeepCloneStateMethods.AddKnownRef, from, local),
                Expression.Convert(local, typeof(object))
            ),
            from, state).Compile();
    }

#if true // MODERN
    private static readonly Lazy<MethodInfo> jsonNodeDeepCloneMethod = new Lazy<MethodInfo>(
        () => typeof(System.Text.Json.Nodes.JsonNode).GetMethod("DeepClone")!,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static object GenerateJsonNodeProcessorModern(ExpressionPosition position)
    {
        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
        Expression castToJsonNode = Expression.Convert(from, typeof(System.Text.Json.Nodes.JsonNode));
        Expression deepCloneCall = Expression.Call(castToJsonNode, jsonNodeDeepCloneMethod.Value);
        Expression result = Expression.Convert(deepCloneCall, typeof(object));

        return Expression.Lambda<Func<object, FastCloneState, object>>(result, from, state).Compile();
    }
#else
    private static object GenerateJsonNodeProcessorNetstandard(Type type, ExpressionPosition position)
    {
        return FastClonerCache.GetOrAddSpecialType(type, t =>
        {
            ParameterExpression from = Expression.Parameter(typeof(object));
            ParameterExpression state = Expression.Parameter(typeof(FastCloneState));
            MethodInfo? deepCloneMethod = t.GetMethod("DeepClone", Type.EmptyTypes);
            
            if (deepCloneMethod is null)
            {
                return Expression.Lambda<Func<object, FastCloneState, object>>(from, from, state).Compile();
            }
            
            Expression castToType = Expression.Convert(from, t);
            Expression deepCloneCall = Expression.Call(castToType, deepCloneMethod);
            Expression result = Expression.Convert(deepCloneCall, typeof(object));
            
            return Expression.Lambda<Func<object, FastCloneState, object>>(result, from, state).Compile();
        });
    }
#endif
}
