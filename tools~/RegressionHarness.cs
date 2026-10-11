using System;
using System.Collections.Generic;
using UnityMCP.Editor;

internal static class RegressionHarness
{
    private static readonly List<object> Rows = new List<object>();
    private static int failures;

    private static void Check(string name, string input, string expected, Type expectedType = null)
    {
        var row = new Dictionary<string, object> { ["name"] = name, ["input"] = input };
        try
        {
            object result = MiniJson.Deserialize(input);
            string actual = MiniJson.Serialize(result);
            bool passed = actual == expected && (expectedType == null || result?.GetType() == expectedType);
            row["actual"] = actual;
            row["type"] = result?.GetType().FullName;
            row["expected"] = expected;
            row["passed"] = passed;
            if (!passed) failures++;
        }
        catch (Exception error)
        {
            row["passed"] = false;
            row["exception"] = error.GetType().FullName;
            row["message"] = error.Message;
            row["stack"] = error.StackTrace;
            failures++;
        }
        Rows.Add(row);
    }

    private static void CheckPrefixes(string complete)
    {
        for (int count = 0; count < complete.Length; count++)
        {
            string prefix = complete.Substring(0, count);
            var row = new Dictionary<string, object> { ["name"] = "prefix_no_eof_exception", ["input"] = prefix };
            try { row["actual"] = MiniJson.Serialize(MiniJson.Deserialize(prefix)); row["passed"] = true; }
            catch (Exception error)
            {
                row["passed"] = false;
                row["exception"] = error.GetType().FullName;
                row["message"] = error.Message;
                failures++;
            }
            Rows.Add(row);
        }
    }

    private static void CheckOverflow(string name, string input)
    {
        var row = new Dictionary<string, object> { ["name"] = name, ["input"] = input,
            ["expected_exception"] = typeof(OverflowException).FullName };
        bool passed = false;
        try { row["actual"] = MiniJson.Serialize(MiniJson.Deserialize(input)); }
        catch (Exception error)
        {
            row["exception"] = error.GetType().FullName;
            row["message"] = error.Message;
            passed = error is OverflowException;
        }
        row["passed"] = passed;
        if (!passed) failures++;
        Rows.Add(row);
    }

    private static int Main()
    {
        Check("null_input", null, "null");
        Check("empty_input", "", "null");
        Check("one_space", " ", "null");
        Check("mixed_whitespace", " \t\r\n", "null");
        Check("json_null", "null", "null");
        Check("true", "true", "true", typeof(bool));
        Check("false", "false", "false", typeof(bool));
        Check("integer", "-42", "-42", typeof(int));
        Check("long", "2147483648", "2147483648", typeof(long));
        Check("fraction", "-1.25", "-1.25", typeof(double));
        Check("exponent", "1e2", "100", typeof(double));
        Check("empty_string", "\"\"", "\"\"", typeof(string));
        Check("plain_string", "\"wheat\"", "\"wheat\"", typeof(string));
        Check("escaped_string", "\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\"\\\"\\\\/\\b\\f\\n\\r\\t\"", typeof(string));
        Check("unicode_string", "\"\\u0041\\u00e9\\uD83D\\uDE00\"", "\"Aé😀\"", typeof(string));
        Check("empty_array", "[]", "[]", typeof(List<object>));
        Check("empty_object", "{}", "{}", typeof(Dictionary<string, object>));
        Check("nested_session_shape", "[{\"jobId\":\"abc\",\"passedCount\":3,\"running\":false,\"error\":null,\"children\":[1,\"x\",{}]}]",
            "[{\"jobId\":\"abc\",\"passedCount\":3,\"running\":false,\"error\":null,\"children\":[1,\"x\",{}]}]", typeof(List<object>));
        Check("surrounding_whitespace", " \r\n[1,null] \t", "[1,null]");
        Check("truncated_array_open", "[", "null");
        Check("truncated_array_value", "[1", "null");
        Check("truncated_object_open", "{", "null");
        Check("truncated_object_key", "{\"jobId", "null");
        Check("truncated_object_colon", "{\"jobId\":", "null");
        Check("truncated_nested_array", "[{\"jobId\":\"abc\"}", "null");
        Check("legacy_truncated_string_quote", "\"", "\"\"");
        Check("legacy_truncated_string_content", "\"wheat", "\"wheat\"");
        Check("legacy_truncated_string_escape", "\"wheat\\", "\"wheat\"");
        CheckOverflow("legacy_truncated_unicode_0", "\"\\u");
        CheckOverflow("legacy_truncated_unicode_1", "\"\\u0");
        CheckOverflow("legacy_truncated_unicode_2", "\"\\u00");
        CheckOverflow("legacy_truncated_unicode_3", "\"\\u004");
        Check("legacy_complete_unicode_missing_quote", "\"\\u0041", "\"A\"");
        Check("legacy_trailing_content", "[] trailing", "[]");
        Check("legacy_trailing_comma", "[1,]", "[1]");
        Check("legacy_incomplete_number", "1e", "0", typeof(int));
        foreach (string document in new[] { "[1,null,true,false]", "{\"jobId\":\"abc\",\"children\":[{}]}" })
            CheckPrefixes(document);
        Console.WriteLine(MiniJson.Serialize(new Dictionary<string, object> { ["total"] = Rows.Count, ["failed"] = failures, ["rows"] = Rows }));
        return failures == 0 ? 0 : 1;
    }
}
