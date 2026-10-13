using System.IO;

namespace NetShift.App.Services;

internal static class ZapretStrategyComparer
{
    public static int ComparePaths(string? leftPath, string? rightPath)
    {
        var left = Path.GetFileName(leftPath);
        var right = Path.GetFileName(rightPath);
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftCharacter = left[leftIndex];
            var rightCharacter = right[rightIndex];
            if (char.IsAsciiDigit(leftCharacter) && char.IsAsciiDigit(rightCharacter))
            {
                var leftEnd = leftIndex;
                var rightEnd = rightIndex;
                while (leftEnd < left.Length && char.IsAsciiDigit(left[leftEnd])) leftEnd++;
                while (rightEnd < right.Length && char.IsAsciiDigit(right[rightEnd])) rightEnd++;

                var leftValueStart = leftIndex;
                var rightValueStart = rightIndex;
                while (leftValueStart < leftEnd - 1 && left[leftValueStart] == '0') leftValueStart++;
                while (rightValueStart < rightEnd - 1 && right[rightValueStart] == '0') rightValueStart++;

                var leftValueLength = leftEnd - leftValueStart;
                var rightValueLength = rightEnd - rightValueStart;
                var comparison = leftValueLength.CompareTo(rightValueLength);
                if (comparison != 0) return comparison;

                comparison = string.CompareOrdinal(left, leftValueStart, right, rightValueStart, leftValueLength);
                if (comparison != 0) return comparison;

                comparison = (leftEnd - leftIndex).CompareTo(rightEnd - rightIndex);
                if (comparison != 0) return comparison;

                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var characterComparison = char.ToUpperInvariant(leftCharacter).CompareTo(char.ToUpperInvariant(rightCharacter));
            if (characterComparison != 0) return characterComparison;
            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }
}
