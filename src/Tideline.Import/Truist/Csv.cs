using System.Text;

namespace Tideline.Import.Truist;

/// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, embedded commas and line breaks.</summary>
internal static class Csv
{
    public static List<List<string>> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();
            // a completely empty line is not a row
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0 || fieldStarted) EndRow();
        return rows;
    }
}
