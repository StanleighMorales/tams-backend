using System.Text;

namespace Tams.Api.Students;

/// <summary>
/// Minimal RFC 4180 reader: quoted fields with embedded commas, doubled quotes and
/// newlines, CRLF or LF line ends, and a leading UTF-8 BOM. Each record carries the
/// physical line number it starts on, so errors can point at the spreadsheet row.
/// </summary>
public static class CsvReader
{
    public sealed record Record(int Line, List<string> Fields);

    public static List<Record> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var records = new List<Record>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var pending = false; // something was read for the current record

        void EndRecord()
        {
            fields.Add(sb.ToString());
            sb.Clear();
            // Skip fully blank lines.
            if (!(fields.Count == 1 && fields[0].Length == 0)) records.Add(new Record(recordLine, fields));
            fields = [];
            pending = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    sb.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when sb.Length == 0: inQuotes = true; pending = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); pending = true; break;
                case '\r': break; // CRLF: the \n ends the record
                case '\n': EndRecord(); line++; recordLine = line; break;
                default: sb.Append(c); pending = true; break;
            }
        }

        if (inQuotes) throw new FormatException($"Unterminated quoted field starting on line {recordLine}.");
        if (pending) EndRecord();
        return records;
    }
}
