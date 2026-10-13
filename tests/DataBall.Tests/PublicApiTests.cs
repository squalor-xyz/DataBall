// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-16: the public surface of the <c>DataBall</c> and <c>DataBall.Handlers</c> assemblies is a checked-in list each
    /// (<c>PublicApi.txt</c>, <c>PublicApi.Handlers.txt</c>). A change to it fails here until the file is regenerated and the diff
    /// reviewed. Regenerate with <c>DATABALL_WRITE_PUBLIC_API=1</c> set when running these tests; they rewrite the files next to this source.
    /// </summary>
    public class PublicApiTests
    {
        private const string WriteVariable = "DATABALL_WRITE_PUBLIC_API";

        [Fact]
        public void PublicApi_MatchesApprovedList() =>
            AssertMatches(typeof(DataBall).Assembly, "PublicApi.txt");

        [Fact]
        public void PublicApi_Handlers_MatchesApprovedList() =>
            AssertMatches(typeof(Handlers.LabHandlers).Assembly, "PublicApi.Handlers.txt");

        private static void AssertMatches(Assembly assembly, string fileName)
        {
            var actual = Describe(assembly);
            if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
            {
                File.WriteAllText(Path.Combine(SourceDirectory(), fileName), actual);
                return;
            }

            var approved = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName)));
            actual = Normalize(actual);
            if (approved == actual)
                return;

            var approvedLines = approved.Split('\n').ToHashSet(StringComparer.Ordinal);
            var actualLines = actual.Split('\n').ToHashSet(StringComparer.Ordinal);
            var diff = new StringBuilder($"The public surface differs from {fileName}.");
            foreach (var line in approvedLines.Except(actualLines).OrderBy(l => l, StringComparer.Ordinal))
                diff.Append("\n- ").Append(line);
            foreach (var line in actualLines.Except(approvedLines).OrderBy(l => l, StringComparer.Ordinal))
                diff.Append("\n+ ").Append(line);
            Assert.Fail(diff.ToString());
        }

        private static string SourceDirectory([CallerFilePath] string sourcePath = "") => Path.GetDirectoryName(sourcePath)!;

        private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

        /// <summary>Every public type and member of <paramref name="assembly"/>, one line each, sorted ordinally.</summary>
        private static string Describe(Assembly assembly)
        {
            var lines = new List<string>();
            foreach (var type in assembly.GetExportedTypes())
            {
                var name = TypeName(type, withNamespace: true);
                lines.Add($"{name} : {Kind(type)}{Bases(type)}{Constraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : Type.EmptyTypes)}");
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var member in type.GetMembers(flags))
                {
                    // Compiler-generated members of records (<Clone>$) are not API.
                    if (member.Name.Contains('<'))
                        continue;
                    var line = Member(type, member);
                    if (line is not null)
                        lines.Add($"{name}.{line}");
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>The base class and every implemented interface of a class or struct, as <c> : A, B</c>; empty for none.</summary>
        private static string Bases(Type type)
        {
            if (type.IsEnum || type.IsInterface || typeof(Delegate).IsAssignableFrom(type))
                return string.Empty;
            var bases = new List<string>();
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
                bases.Add(TypeName(baseType, withNamespace: true));
            bases.AddRange(type.GetInterfaces().Select(i => TypeName(i, withNamespace: true)).OrderBy(n => n, StringComparer.Ordinal));
            return bases.Count == 0 ? string.Empty : " : " + string.Join(", ", bases);
        }

        private static string Kind(Type type)
        {
            if (type.IsEnum)
                return "enum";
            if (type.IsInterface)
                return "interface";
            if (type.IsValueType)
                return "struct";
            if (typeof(Delegate).IsAssignableFrom(type))
                return "delegate";
            if (type.IsAbstract && type.IsSealed)
                return "static class";
            return (type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : string.Empty) + "class";
        }

        private static string? Member(Type owner, MemberInfo member)
        {
            switch (member)
            {
                case ConstructorInfo c when c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly:
                    return $"{(c.IsStatic ? "static " : string.Empty)}.ctor({Parameters(c.GetParameters())})";
                case MethodInfo m when (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)):
                    return $"{Modifiers(m)}{m.Name}{Generic(m)}({Parameters(m.GetParameters())}) : {Annotated(m.ReturnParameter)}{Constraints(m.GetGenericArguments())}";
                case PropertyInfo p when p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly):
                    {
                        var accessors = new List<string>();
                        if (p.GetMethod is { } get && (get.IsPublic || get.IsFamily || get.IsFamilyOrAssembly))
                            accessors.Add("get");
                        if (p.SetMethod is { } set && (set.IsPublic || set.IsFamily || set.IsFamilyOrAssembly))
                            accessors.Add(IsInitOnly(set) ? "init" : "set");
                        var index = p.GetIndexParameters();
                        var name = index.Length == 0 ? p.Name : $"{p.Name}[{Parameters(index)}]";
                        var isStatic = (p.GetMethod ?? p.SetMethod)!.IsStatic;
                        return $"{(isStatic ? "static " : string.Empty)}{Required(p)}{name} : {Annotated(p)} {{ {string.Join(" ", accessors)} }}";
                    }
                case FieldInfo f when (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) && !f.IsSpecialName:
                    return f.IsLiteral
                        ? $"const {f.Name} : {TypeName(f.FieldType)} = {f.GetRawConstantValue()}"
                        : $"{(f.IsStatic ? "static " : string.Empty)}{Required(f)}{(f.IsInitOnly ? "readonly " : string.Empty)}{f.Name} : {Annotated(f)}";
                case EventInfo e when (e.AddMethod?.IsPublic ?? false) || (e.AddMethod?.IsFamily ?? false):
                    return $"{(e.AddMethod!.IsStatic ? "static " : string.Empty)}event {e.Name} : {TypeName(e.EventHandlerType!)}";
                default:
                    return null;
            }
        }

        private static string Required(MemberInfo member) =>
            member.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute") ? "required " : string.Empty;

        private static string Modifiers(MethodInfo m)
        {
            var text = m.IsStatic ? "static " : string.Empty;
            if (m.IsAbstract)
                text += "abstract ";
            else if (m.IsVirtual && !m.IsFinal)
                text += "virtual ";
            return text;
        }

        private static string Generic(MethodInfo m) =>
            m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

        /// <summary>The constraints on each generic parameter, one <c> where T : struct</c> clause per parameter; empty when there are none.</summary>
        private static string Constraints(Type[] genericArguments)
        {
            var clauses = new StringBuilder();
            foreach (var parameter in genericArguments)
            {
                var attributes = parameter.GenericParameterAttributes;
                // The compiler encodes `struct` as the ValueType constraint plus the new() flag; C# writes it as the one keyword.
                var isStruct = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                var terms = new List<string>();
                if (isStruct)
                    terms.Add("struct");
                else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    terms.Add("class");
                terms.AddRange(parameter.GetGenericParameterConstraints()
                    .Where(c => !isStruct || c != typeof(ValueType))
                    .Select(c => TypeName(c)));
                if (!isStruct && (attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    terms.Add("new()");
                if (terms.Count > 0)
                    clauses.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", terms));
            }
            return clauses.ToString();
        }

        private static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        private static string Parameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(p =>
            {
                var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty;
                var text = $"{prefix}{Annotated(p)} {p.Name}";
                if (p.HasDefaultValue)
                    text += " = " + (p.DefaultValue is null || p.DefaultValue == DBNull.Value ? "default" : p.DefaultValue);
                return text;
            }));

        private static string Annotated(ParameterInfo parameter) =>
            WithNullability(parameter.ParameterType, new NullabilityInfoContext().Create(parameter));

        private static string Annotated(PropertyInfo property) =>
            WithNullability(property.PropertyType, new NullabilityInfoContext().Create(property));

        private static string Annotated(FieldInfo field) =>
            WithNullability(field.FieldType, new NullabilityInfoContext().Create(field));

        private static string WithNullability(Type type, NullabilityInfo info)
        {
            var plain = type.IsByRef ? type.GetElementType()! : type;
            var name = TypeName(plain);
            return !plain.IsValueType && info.ReadState == NullabilityState.Nullable ? name + "?" : name;
        }

        private static string TypeName(Type type, bool withNamespace = false)
        {
            if (type.IsGenericParameter)
                return type.Name;
            if (type.IsByRef)
                return TypeName(type.GetElementType()!);
            if (type.IsArray)
                return TypeName(type.GetElementType()!) + "[]";
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return TypeName(nullable) + "?";
            var name = type.IsNested ? TypeName(type.DeclaringType!, withNamespace) + "." + Trim(type.Name) : (withNamespace ? type.Namespace + "." : string.Empty) + Trim(type.Name);
            if (type.IsGenericType)
                name += "<" + string.Join(", ", type.GetGenericArguments().Select(a => TypeName(a))) + ">";
            return name;
        }

        private static string Trim(string name)
        {
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name[..tick];
        }
    }
}
