namespace Marconian.ResearchAgent.Utilities;

public static class VectorMath
{
    public static double CosineSimilarity(IReadOnlyList<float> vectorA, IReadOnlyList<float> vectorB)
    {
        if (vectorA.Count == 0 || vectorB.Count == 0)
        {
            return 0d;
        }

        int length = Math.Min(vectorA.Count, vectorB.Count);
        double dot = 0d;
        double magnitudeA = 0d;
        double magnitudeB = 0d;

        for (int i = 0; i < length; i++)
        {
            double a = vectorA[i];
            double b = vectorB[i];
            dot += a * b;
            magnitudeA += a * a;
            magnitudeB += b * b;
        }

        if (magnitudeA == 0 || magnitudeB == 0)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
    }
}
