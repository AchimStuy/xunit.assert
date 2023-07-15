#if XUNIT_NULLABLE
#nullable enable
#else
// In case this is source-imported with global nullable enabled but no XUNIT_NULLABLE
#pragma warning disable CS8601
#pragma warning disable CS8605
#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8767
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

#if XUNIT_NULLABLE
using System.Diagnostics.CodeAnalysis;
#endif

namespace Xunit.Sdk
{
	/// <summary>
	/// Default implementation of <see cref="IEqualityComparer{T}"/> used by the xUnit.net equality assertions
	/// (except for collections, which are handled directly by the appropriate assertion methods).
	/// </summary>
	/// <typeparam name="T">The type that is being compared.</typeparam>
	class AssertEqualityComparer<T> : IEqualityComparer<T>
	{
		static readonly IEqualityComparer DefaultInnerComparer = new AssertEqualityComparerAdapter<object>(new AssertEqualityComparer<object>());
		static readonly TypeInfo NullableTypeInfo = typeof(Nullable<>).GetTypeInfo();

		readonly Lazy<IEqualityComparer> innerComparer;

		/// <summary>
		/// Initializes a new instance of the <see cref="AssertEqualityComparer{T}" /> class.
		/// </summary>
		/// <param name="innerComparer">The inner comparer to be used when the compared objects are enumerable.</param>
#if XUNIT_NULLABLE
		public AssertEqualityComparer(IEqualityComparer? innerComparer = null)
#else
		public AssertEqualityComparer(IEqualityComparer innerComparer = null)
#endif
		{
			// Use a thunk to delay evaluation of DefaultInnerComparer
			this.innerComparer = new Lazy<IEqualityComparer>(() => innerComparer ?? AssertEqualityComparer<T>.DefaultInnerComparer);
		}

		public IEqualityComparer InnerComparer =>
			innerComparer.Value;

		/// <inheritdoc/>
		public bool Equals(
#if XUNIT_NULLABLE
			[AllowNull] T x,
			[AllowNull] T y)
#else
			T x,
			T y)
#endif
		{
			var typeInfo = typeof(T).GetTypeInfo();

			// Null?
			if (x == null && y == null)
				return true;
			if (x == null || y == null)
				return false;

#if !XUNIT_FRAMEWORK
			// Collections?
			using (var xTracker = x.AsNonStringTracker())
			using (var yTracker = y.AsNonStringTracker())
			{
				int? _;

				if (xTracker != null && yTracker != null)
					return CollectionTracker.AreCollectionsEqual(xTracker, yTracker, InnerComparer, out _);
			}
#endif

			// Implements IEquatable<T>?
			var equatable = x as IEquatable<T>;
			if (equatable != null)
				return equatable.Equals(y);

			// Implements IEquatable<typeof(y)>?
			var iequatableY = typeof(IEquatable<>).MakeGenericType(y.GetType()).GetTypeInfo();
			if (iequatableY.IsAssignableFrom(x.GetType().GetTypeInfo()))
			{
				var equalsMethod = iequatableY.GetDeclaredMethod(nameof(IEquatable<T>.Equals));
				if (equalsMethod == null)
					return false;

#if XUNIT_NULLABLE
				return equalsMethod.Invoke(x, new object[] { y }) is true;
#else
				return (bool)equalsMethod.Invoke(x, new object[] { y });
#endif
			}

			// Implements IStructuralEquatable?
			var structuralEquatable = x as IStructuralEquatable;
			if (structuralEquatable != null && structuralEquatable.Equals(y, new TypeErasedEqualityComparer(innerComparer.Value)))
				return true;

			// Implements IComparable<T>?
			var comparableGeneric = x as IComparable<T>;
			if (comparableGeneric != null)
			{
				try
				{
					return comparableGeneric.CompareTo(y) == 0;
				}
				catch
				{
					// Some implementations of IComparable<T>.CompareTo throw exceptions in
					// certain situations, such as if x can't compare against y.
					// If this happens, just swallow up the exception and continue comparing.
				}
			}

			// Implements IComparable<typeof(y)>?
			var icomparableY = typeof(IComparable<>).MakeGenericType(y.GetType()).GetTypeInfo();
			if (icomparableY.IsAssignableFrom(x.GetType().GetTypeInfo()))
			{
				var compareToMethod = icomparableY.GetDeclaredMethod(nameof(IComparable<T>.CompareTo));
				if (compareToMethod == null)
					return false;

				try
				{
#if XUNIT_NULLABLE
					return compareToMethod.Invoke(x, new object[] { y }) is 0;
#else
					return (int)compareToMethod.Invoke(x, new object[] { y }) == 0;
#endif
				}
				catch
				{
					// Some implementations of IComparable.CompareTo throw exceptions in
					// certain situations, such as if x can't compare against y.
					// If this happens, just swallow up the exception and continue comparing.
				}
			}

			// Implements IComparable?
			var comparable = x as IComparable;
			if (comparable != null)
			{
				try
				{
					return comparable.CompareTo(y) == 0;
				}
				catch
				{
					// Some implementations of IComparable.CompareTo throw exceptions in
					// certain situations, such as if x can't compare against y.
					// If this happens, just swallow up the exception and continue comparing.
				}
			}

			// Last case, rely on object.Equals
			return object.Equals(x, y);
		}

#if XUNIT_NULLABLE
		public static IEqualityComparer<T?> FromComparer(Func<T, T, bool> comparer) =>
#else
		public static IEqualityComparer<T> FromComparer(Func<T, T, bool> comparer) =>
#endif
			new FuncEqualityComparer(comparer);

		/// <inheritdoc/>
		public int GetHashCode(T obj)
		{
			throw new NotImplementedException();
		}

#if XUNIT_NULLABLE
		class FuncEqualityComparer : IEqualityComparer<T?>
#else
		class FuncEqualityComparer : IEqualityComparer<T>
#endif
		{
			readonly Func<T, T, bool> comparer;

			public FuncEqualityComparer(Func<T, T, bool> comparer)
			{
				if (comparer == null)
					throw new ArgumentNullException(nameof(comparer));

				this.comparer = comparer;
			}

			public bool Equals(
#if XUNIT_NULLABLE
				T? x,
				T? y)
#else
				T x,
				T y)
#endif
			{
				if (x == null)
					return y == null;

				if (y == null)
					return false;

				return comparer(x, y);
			}

#if XUNIT_NULLABLE
			public int GetHashCode(T? obj)
#else
			public int GetHashCode(T obj)
#endif
			{
				throw new NotImplementedException();
			}
		}

		class TypeErasedEqualityComparer : IEqualityComparer
		{
			readonly IEqualityComparer innerComparer;

			public TypeErasedEqualityComparer(IEqualityComparer innerComparer)
			{
				this.innerComparer = innerComparer;
			}

#if XUNIT_NULLABLE
			static MethodInfo? s_equalsMethod;
#else
			static MethodInfo s_equalsMethod;
#endif

			public new bool Equals(
#if XUNIT_NULLABLE
				object? x,
				object? y)
#else
				object x,
				object y)
#endif
			{
				if (x == null)
					return y == null;
				if (y == null)
					return false;

				// Delegate checking of whether two objects are equal to AssertEqualityComparer.
				// To get the best result out of AssertEqualityComparer, we attempt to specialize the
				// comparer for the objects that we are checking.
				// If the objects are the same, great! If not, assume they are objects.
				// This is more naive than the C# compiler which tries to see if they share any interfaces
				// etc. but that's likely overkill here as AssertEqualityComparer<object> is smart enough.
				Type objectType = x.GetType() == y.GetType() ? x.GetType() : typeof(object);

				// Lazily initialize and cache the EqualsGeneric<U> method.
				if (s_equalsMethod == null)
				{
					s_equalsMethod = typeof(TypeErasedEqualityComparer).GetTypeInfo().GetDeclaredMethod(nameof(EqualsGeneric));
					if (s_equalsMethod == null)
						return false;
				}

#if XUNIT_NULLABLE
				return s_equalsMethod.MakeGenericMethod(objectType).Invoke(this, new object[] { x, y }) is true;
#else
				return (bool)s_equalsMethod.MakeGenericMethod(objectType).Invoke(this, new object[] { x, y });
#endif
			}

			bool EqualsGeneric<U>(
				U x,
				U y) =>
					new AssertEqualityComparer<U>(innerComparer: innerComparer).Equals(x, y);

			public int GetHashCode(object obj)
			{
				throw new NotImplementedException();
			}
		}
	}
}
