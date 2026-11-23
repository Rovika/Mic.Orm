namespace Mic.Linq;

/// <summary>Provides the functions over vectors that a query writes as the server's own.</summary>
public static class VectorFunctions {
  /// <summary>Returns the distance between two vectors; a smaller distance is a nearer vector.</summary>
  /// <param name="vector">The first vector.</param>
  /// <param name="other">The second vector.</param>
  /// <returns>The distance.</returns>
  /// <exception cref="ArgumentException">The vectors differ in length.</exception>
  /// <remarks>Inside a query the server measures the distance, by the metric the vector member among the two
  /// declares, and by the cosine metric where neither is a vector member. In plain code it is the cosine distance:
  /// one minus the cosine of the angle between the vectors.</remarks>
  public static double Distance(float[] vector, float[] other) {
    ArgumentNullException.ThrowIfNull(vector);
    ArgumentNullException.ThrowIfNull(other);
    if (vector.Length != other.Length)
      throw new ArgumentException($"The vectors differ in length, {vector.Length} and {other.Length}.", nameof(other));
    double product = 0, norm = 0, otherNorm = 0;
    for (var i = 0; i < vector.Length; i++) {
      product += (double)vector[i] * other[i];
      norm += (double)vector[i] * vector[i];
      otherNorm += (double)other[i] * other[i];
    }
    return 1 - product / Math.Sqrt(norm * otherNorm);
  }
}
