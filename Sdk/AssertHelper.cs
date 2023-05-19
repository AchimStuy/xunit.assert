#if XUNIT_NULLABLE
#nullable enable

using System.Diagnostics.CodeAnalysis;
#endif

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit.Sdk;

namespace Xunit.Internal
{
	internal static class AssertHelper
	{
		static readonly Dictionary<char, string> encodings = new Dictionary<char, string>
		{
			{ '\0', @"\0" },  // Null
			{ '\a', @"\a" },  // Alert
			{ '\b', @"\b" },  // Backspace
			{ '\f', @"\f" },  // Form feed
			{ '\n', @"\n" },  // New line
			{ '\r', @"\r" },  // Carriage return
			{ '\t', @"\t" },  // Horizontal tab
			{ '\v', @"\v" },  // Vertical tab
			{ '\\', @"\\" },  // Backslash
		};

#if XUNIT_NULLABLE
		static ConcurrentDictionary<Type, Dictionary<string, Func<object?, object?>>> gettersByType = new ConcurrentDictionary<Type, Dictionary<string, Func<object?, object?>>>();
#else
		static ConcurrentDictionary<Type, Dictionary<string, Func<object, object>>> gettersByType = new ConcurrentDictionary<Type, Dictionary<string, Func<object, object>>>();
#endif

#if XUNIT_NULLABLE
		static Dictionary<string, Func<object?, object?>> GetGettersForType(Type type) =>
#else
		static Dictionary<string, Func<object, object>> GetGettersForType(Type type) =>
#endif
			gettersByType.GetOrAdd(type, _type =>
			{
				var fieldGetters =
					_type
						.GetRuntimeFields()
						.Where(f => f.IsPublic && !f.IsStatic)
#if XUNIT_NULLABLE
						.Select(f => new { name = f.Name, getter = (Func<object?, object?>)f.GetValue });
#else
						.Select(f => new { name = f.Name, getter = (Func<object, object>)f.GetValue });
#endif

				var propertyGetters =
					_type
						.GetRuntimeProperties()
						.Where(p => p.CanRead && p.GetMethod != null && p.GetMethod.IsPublic && !p.GetMethod.IsStatic && p.GetIndexParameters().Length == 0)
#if XUNIT_NULLABLE
						.Select(p => new { name = p.Name, getter = (Func<object?, object?>)p.GetValue });
#else
						.Select(p => new { name = p.Name, getter = (Func<object, object>)p.GetValue });
#endif

				return
					fieldGetters
						.Concat(propertyGetters)
						.ToDictionary(g => g.name, g => g.getter);
			});

		internal static string ShortenAndEncodeString(
#if XUNIT_NULLABLE
			string? value,
#else
			string value,
#endif
			int index,
			out int pointerIndent)
		{
			if (value == null)
			{
				pointerIndent = -1;
				return "null";
			}

			var start = Math.Max(index - 20, 0);
			var end = Math.Min(start + 41, value.Length);
			start = Math.Max(end - 41, 0);
			var printedValue = new StringBuilder(100);
			pointerIndent = 0;

			if (start > 0)
			{
				printedValue.Append(ArgumentFormatter.Ellipsis);
				pointerIndent += 3;
			}

			printedValue.Append('\"');
			pointerIndent++;

			for (var idx = start; idx < end; ++idx)
			{
				var c = value[idx];
				var paddingLength = 1;

#if XUNIT_NULLABLE
				string? encoding;
#else
				string encoding;
#endif

				if (encodings.TryGetValue(c, out encoding))
				{
					printedValue.Append(encoding);
					paddingLength = encoding.Length;
				}
				else
					printedValue.Append(c);

				if (idx < index)
					pointerIndent += paddingLength;
			}

			printedValue.Append('\"');

			if (end < value.Length)
				printedValue.Append(ArgumentFormatter.Ellipsis);

			return printedValue.ToString();
		}

#if XUNIT_NULLABLE
		internal static string ShortenAndEncodeString(string? value)
#else
		internal static string ShortenAndEncodeString(string value)
#endif
		{
			int pointerIndent;

			return ShortenAndEncodeString(value, 0, out pointerIndent);
		}

#if XUNIT_NULLABLE
		internal static string ShortenAndEncodeStringEnd(string? value)
#else
		internal static string ShortenAndEncodeStringEnd(string value)
#endif
		{
			int pointerIndent;

			return ShortenAndEncodeString(value, (value?.Length - 1) ?? 0, out pointerIndent);
		}

		static bool TryConvert(
			object value,
			Type targetType,
#if XUNIT_NULLABLE
			[NotNullWhen(true)] out object? converted)
#else
			out object converted)
#endif
		{
			converted = Convert.ChangeType(value, targetType);
			return converted != null;
		}

#if XUNIT_NULLABLE
		public static EquivalentException? VerifyEquivalence(
			object? expected,
			object? actual,
#else
		public static EquivalentException VerifyEquivalence(
			object expected,
			object actual,
#endif
			bool strict)
		{
			return VerifyEquivalence(expected, actual, strict, string.Empty, new HashSet<object>(), new HashSet<object>());
		}

#if XUNIT_NULLABLE
		static EquivalentException? VerifyEquivalence(
			object? expected,
			object? actual,
#else
		static EquivalentException VerifyEquivalence(
			object expected,
			object actual,
#endif
			bool strict,
			string prefix,
			HashSet<object> expectedRefs,
			HashSet<object> actualRefs)
		{
			// Check for null equivalence
			if (expected == null)
				return
					actual == null
						? null
						: EquivalentException.ForMemberValueMismatch(expected, actual, prefix);

			if (actual == null)
				return EquivalentException.ForMemberValueMismatch(expected, actual, prefix);

			// Check for identical references
			if (ReferenceEquals(expected, actual))
				return null;

			// Prevent circular references
			if (expectedRefs.Contains(expected))
				return EquivalentException.ForCircularReference($"{nameof(expected)}.{prefix}");

			if (actualRefs.Contains(actual))
				return EquivalentException.ForCircularReference($"{nameof(actual)}.{prefix}");

			expectedRefs.Add(expected);
			actualRefs.Add(actual);

			try
			{
				var expectedType = expected.GetType();
				var expectedTypeInfo = expectedType.GetTypeInfo();

				// Primitive types, enums and strings should just fall back to their Equals implementation
				if (expectedTypeInfo.IsPrimitive || expectedTypeInfo.IsEnum || expectedType == typeof(string))
					return VerifyEquivalenceIntrinsics(expected, actual, prefix);

				// IComparable value types should fall back to their CompareTo implementation
				if (expectedTypeInfo.IsValueType)
				{
					// TODO: Should we support more than just IComparable? This feels like it was added solely
					// to support DateTime (a built-in non-intrinsic value type). Should we support the full
					// gamut of everything that we do in AssertEqualityComparer?
					try
					{
						var expectedComparable = expected as IComparable;
						if (expectedComparable != null)
							return
								expectedComparable.CompareTo(actual) == 0
									? null
								: EquivalentException.ForMemberValueMismatch(expected, actual, prefix);
					}
					catch (Exception ex)
					{
						return EquivalentException.ForMemberValueMismatch(expected, actual, prefix, ex);
					}

					try
					{
						var actualComparable = actual as IComparable;
						if (actualComparable != null)
							return
								actualComparable.CompareTo(expected) == 0
									? null
									: EquivalentException.ForMemberValueMismatch(expected, actual, prefix);
					}
					catch (Exception ex)
					{
						return EquivalentException.ForMemberValueMismatch(expected, actual, prefix, ex);
					}
				}

				// Enumerables? Check equivalence of individual members
				var enumerableExpected = expected as IEnumerable;
				var enumerableActual = actual as IEnumerable;
				if (enumerableExpected != null && enumerableActual != null)
					return VerifyEquivalenceEnumerable(enumerableExpected, enumerableActual, strict, prefix, expectedRefs, actualRefs);

				return VerifyEquivalenceReference(expected, actual, strict, prefix, expectedRefs, actualRefs);
			}
			finally
			{
				expectedRefs.Remove(expected);
				actualRefs.Remove(actual);
			}
		}

#if XUNIT_NULLABLE
		static EquivalentException? VerifyEquivalenceEnumerable(
#else
		static EquivalentException VerifyEquivalenceEnumerable(
#endif
			IEnumerable expected,
			IEnumerable actual,
			bool strict,
			string prefix,
			HashSet<object> expectedRefs,
			HashSet<object> actualRefs)
		{
#if XUNIT_NULLABLE
			var expectedValues = expected.Cast<object?>().ToList();
			var actualValues = actual.Cast<object?>().ToList();
#else
			var expectedValues = expected.Cast<object>().ToList();
			var actualValues = actual.Cast<object>().ToList();
#endif
			var actualOriginalValues = actualValues.ToList();

			// Walk the list of expected values, and look for actual values that are equivalent
			foreach (var expectedValue in expectedValues)
			{
				var actualIdx = 0;
				for (; actualIdx < actualValues.Count; ++actualIdx)
					if (VerifyEquivalence(expectedValue, actualValues[actualIdx], strict, "", expectedRefs, actualRefs) == null)
						break;

				if (actualIdx == actualValues.Count)
					return EquivalentException.ForMissingCollectionValue(expectedValue, actualOriginalValues, prefix);

				actualValues.RemoveAt(actualIdx);
			}

			if (strict && actualValues.Count != 0)
				return EquivalentException.ForExtraCollectionValue(expectedValues, actualOriginalValues, actualValues, prefix);

			return null;
		}

#if XUNIT_NULLABLE
		static EquivalentException? VerifyEquivalenceIntrinsics(
#else
		static EquivalentException VerifyEquivalenceIntrinsics(
#endif
			object expected,
			object actual,
			string prefix)
		{
			var result = expected.Equals(actual);

			var converted = default(object);
			if (!result && TryConvert(expected, actual.GetType(), out converted))
				result = converted.Equals(actual);
			if (!result && TryConvert(actual, expected.GetType(), out converted))
				result = converted.Equals(expected);

			return result ? null : EquivalentException.ForMemberValueMismatch(expected, actual, prefix);
		}

#if XUNIT_NULLABLE
		static EquivalentException? VerifyEquivalenceReference(
#else
		static EquivalentException VerifyEquivalenceReference(
#endif
			object expected,
			object actual,
			bool strict,
			string prefix,
			HashSet<object> expectedRefs,
			HashSet<object> actualRefs)
		{
			var prefixDot = prefix == string.Empty ? string.Empty : prefix + ".";

			// Enumerate over public instance fields and properties and validate equivalence
			var expectedGetters = GetGettersForType(expected.GetType());
			var actualGetters = GetGettersForType(actual.GetType());

			if (strict && expectedGetters.Count != actualGetters.Count)
				return EquivalentException.ForMemberListMismatch(expectedGetters.Keys, actualGetters.Keys, prefixDot);

			foreach (var kvp in expectedGetters)
			{
#if XUNIT_NULLABLE
				Func<object?, object?>? actualGetter;
#else
				Func<object, object> actualGetter;
#endif

				if (!actualGetters.TryGetValue(kvp.Key, out actualGetter))
					return EquivalentException.ForMemberListMismatch(expectedGetters.Keys, actualGetters.Keys, prefixDot);

				var expectedMemberValue = kvp.Value(expected);
				var actualMemberValue = actualGetter(actual);

				var ex = VerifyEquivalence(expectedMemberValue, actualMemberValue, strict, prefixDot + kvp.Key, expectedRefs, actualRefs);
				if (ex != null)
					return ex;
			}

			return null;
		}
	}
}
