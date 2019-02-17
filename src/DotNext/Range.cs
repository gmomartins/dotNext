using System;

namespace DotNext
{
	/// <summary>
	/// Range checks.
	/// </summary>
    public static class Range
    {
        /// <summary>
        /// Restricts a <paramref name="value" /> in specific range.
        /// </summary>
        /// <typeparam name="T">Type of the values.</typeparam>
        /// <param name="value">Value to be restricted.</param>
        /// <param name="min">Minimal range value.</param>
        /// <param name="max">Maximum range value.</param>
        public static T Clamp<T> (this T value, T min, T max) 
            where T : IComparable<T>
            => value.UpperBounded(max).LowerBounded(min);

        /// <summary>
        /// Restricts a <paramref name="value" /> minimal value.
        /// </summary>
        /// <typeparam name="T">Type of the values.</typeparam>
        /// <param name="value">The value to be restricted.</param>
        /// <param name="min">The lower bound.</param>
        public static T LowerBounded<T> (this T value, T min) where T : IComparable<T>
            => value.CompareTo (min) < 0 ? min : value;

		/// <summary>
		/// Restricts a <paramref name="value" /> maximum value.
		/// </summary>
		/// <typeparam name="T">Type of the values.</typeparam>
		/// <param name="value">The value to be restricted.</param>
		/// <param name="max">The upper bound.</param>
		public static T UpperBounded<T>(this T value, T max) where T : IComparable<T>
			=> value.CompareTo(max) > 0 ? max : value;

		/// <summary>
		/// Checks whether specified value is in range.
		/// </summary>
		/// <typeparam name="T">Type of value to check.</typeparam>
		/// <param name="value">Value to check.</param>
		/// <param name="left">Range left bound.</param>
		/// <param name="right">Range right bound.</param>
		/// <param name="boundType">Range endpoints bound type.</param>
		/// <returns><see langword="true"/>, if <paramref name="value"/> is in its bounds.</returns>
        public static bool Between<T>(this T value, T left, T right, BoundType boundType = BoundType.Open)
            where T: IComparable<T>
        {
            int leftCmp = value.CompareTo(left), rightCmp = value.CompareTo(right);
            switch(boundType)
            {
                case BoundType.Open:
                    return leftCmp > 0 && rightCmp < 0;
                case BoundType.LeftClosed:
                    return leftCmp >= 0 && rightCmp < 0;
                case BoundType.RightClosed:
                    return leftCmp > 0 && rightCmp <= 0;
                case BoundType.Closed:
					return leftCmp >= 0 && rightCmp <= 0;
                default:
                    return false;
            }
        }
    }
}