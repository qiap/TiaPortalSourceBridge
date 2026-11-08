using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class DiffLine
    {
        public string type { get; set; } // "same", "add", "delete"
        public int? oldLine { get; set; }
        public int? newLine { get; set; }
        public string text { get; set; }
    }

    internal sealed class FileDiffResult
    {
        public string fileName { get; set; }
        public string oldPath { get; set; }
        public string newPath { get; set; }
        public int additions { get; set; }
        public int deletions { get; set; }
        public List<DiffLine> lines { get; set; }
        public bool isBinary { get; set; }
        public string error { get; set; }
        public SemanticDiffResult semantic { get; set; }
        public object evidence { get; set; }

        public FileDiffResult()
        {
            lines = new List<DiffLine>();
        }
    }

    internal static class DiffHelper
    {
        public static FileDiffResult CompareFiles(string oldFilePath, string newFilePath, string displayName = null)
        {
            var result = new FileDiffResult
            {
                fileName = displayName ?? Path.GetFileName(newFilePath ?? oldFilePath),
                oldPath = oldFilePath,
                newPath = newFilePath
            };

            try
            {
                if (!File.Exists(oldFilePath) || !File.Exists(newFilePath)) throw new IOException("对比文件缺失，不能将缺失源码视为删除。");
                if (new FileInfo(oldFilePath).Length > 8 * 1024 * 1024 || new FileInfo(newFilePath).Length > 8 * 1024 * 1024) throw new IOException("单个对比文件不得超过 8 MiB。");
                var compared = CompareText(File.ReadAllText(oldFilePath, Encoding.UTF8), File.ReadAllText(newFilePath, Encoding.UTF8), result.fileName);
                compared.oldPath = oldFilePath; compared.newPath = newFilePath;
                return compared;
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
                return result;
            }
        }

        public static FileDiffResult CompareText(string oldText, string newText, string displayName)
        {
            var result = new FileDiffResult
            {
                fileName = displayName
            };

            if (oldText == null || newText == null || oldText.Length > 8 * 1024 * 1024 || newText.Length > 8 * 1024 * 1024) { result.error = "源码缺失或超过大小限制。"; return result; }
            if (TooManyLines(oldText) || TooManyLines(newText)) { result.error = "单份源码超过 100000 行，请缩小对比对象。"; return result; }
            result.semantic = SemanticDiff.Compare(oldText, newText);
            string[] oldLines = string.IsNullOrEmpty(oldText) ? new string[0] : oldText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string[] newLines = string.IsNullOrEmpty(newText) ? new string[0] : newText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            return ComputeDiff(oldLines, newLines, result);
        }

        private static bool TooManyLines(string text)
        {
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\r' || text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                    if (++count > 100000) return true;
            return false;
        }

        private static FileDiffResult ComputeDiff(string[] oldLines, string[] newLines, FileDiffResult result)
        {
            int n = oldLines.Length;
            int m = newLines.Length;

            // Trim common prefix
            int start = 0;
            while (start < n && start < m && string.Equals(oldLines[start], newLines[start], StringComparison.Ordinal))
            {
                result.lines.Add(new DiffLine
                {
                    type = "same",
                    oldLine = start + 1,
                    newLine = start + 1,
                    text = oldLines[start]
                });
                start++;
            }

            // Trim common suffix
            int endOld = n - 1;
            int endNew = m - 1;
            var suffix = new List<DiffLine>();
            while (endOld >= start && endNew >= start && string.Equals(oldLines[endOld], newLines[endNew], StringComparison.Ordinal))
            {
                suffix.Add(new DiffLine
                {
                    type = "same",
                    oldLine = endOld + 1,
                    newLine = endNew + 1,
                    text = oldLines[endOld]
                });
                endOld--;
                endNew--;
            }

            // Compute LCS for middle section if within reasonable bounds (up to 2000 lines)
            int midOldLen = endOld - start + 1;
            int midNewLen = endNew - start + 1;

            if (midOldLen > 0 && midNewLen > 0 && (long)midOldLen * midNewLen <= 4000000)
            {
                int[,] lcs = new int[midOldLen + 1, midNewLen + 1];
                for (int i = 0; i < midOldLen; i++)
                {
                    for (int j = 0; j < midNewLen; j++)
                    {
                        if (string.Equals(oldLines[start + i], newLines[start + j], StringComparison.Ordinal))
                            lcs[i + 1, j + 1] = lcs[i, j] + 1;
                        else
                            lcs[i + 1, j + 1] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                    }
                }

                // Backtrack
                var diffMiddle = new List<DiffLine>();
                int currI = midOldLen;
                int currJ = midNewLen;

                while (currI > 0 || currJ > 0)
                {
                    if (currI > 0 && currJ > 0 && string.Equals(oldLines[start + currI - 1], newLines[start + currJ - 1], StringComparison.Ordinal))
                    {
                        diffMiddle.Add(new DiffLine
                        {
                            type = "same",
                            oldLine = start + currI,
                            newLine = start + currJ,
                            text = oldLines[start + currI - 1]
                        });
                        currI--;
                        currJ--;
                    }
                    else if (currJ > 0 && (currI == 0 || lcs[currI, currJ - 1] >= lcs[currI - 1, currJ]))
                    {
                        diffMiddle.Add(new DiffLine
                        {
                            type = "add",
                            oldLine = null,
                            newLine = start + currJ,
                            text = newLines[start + currJ - 1]
                        });
                        result.additions++;
                        currJ--;
                    }
                    else if (currI > 0 && (currJ == 0 || lcs[currI, currJ - 1] < lcs[currI - 1, currJ]))
                    {
                        diffMiddle.Add(new DiffLine
                        {
                            type = "delete",
                            oldLine = start + currI,
                            newLine = null,
                            text = oldLines[start + currI - 1]
                        });
                        result.deletions++;
                        currI--;
                    }
                }

                diffMiddle.Reverse();
                result.lines.AddRange(diffMiddle);
            }
            else
            {
                // Fallback for large files: mark all old as deleted and all new as added
                for (int i = start; i <= endOld; i++)
                {
                    result.lines.Add(new DiffLine
                    {
                        type = "delete",
                        oldLine = i + 1,
                        newLine = null,
                        text = oldLines[i]
                    });
                    result.deletions++;
                }
                for (int j = start; j <= endNew; j++)
                {
                    result.lines.Add(new DiffLine
                    {
                        type = "add",
                        oldLine = null,
                        newLine = j + 1,
                        text = newLines[j]
                    });
                    result.additions++;
                }
            }

            suffix.Reverse();
            result.lines.AddRange(suffix);
            return result;
        }
    }
}
