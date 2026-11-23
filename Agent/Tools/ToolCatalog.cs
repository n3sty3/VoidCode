using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace VoidCode.Agent.Tools;

/// <summary>
/// Which tools exist, what the model is told about them, and which ones need approval
/// Or are refused in plan mode. Nothing here executes anything - that is <see cref="ToolExecutor"/>.
/// </summary>
internal static class ToolCatalog
{
    internal static readonly HashSet<string> ApprovalRequiredTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "patch_file", "delete_file", "move_file", "run_command", "web_fetch"
    };

    // Strictly read-only tools allowed during plan mode. Every mutating or
    // Process-running tool is refused server-side so "plan" can never act.
    internal static readonly HashSet<string> PlanModeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "view_image", "list_dir", "search_code", "search_codebase", "git_status_diff"
    };


    /// <summary>Exposed for tests: plan mode must never allow a mutating or process-running tool.</summary>
    internal static bool IsAllowedInPlanMode(string toolName) => PlanModeTools.Contains(toolName);

    /// <summary>The JSON schema array sent as the request's "tools" field.</summary>
    internal static JsonArray Definitions(bool planOnly = false)
    {
        var defs = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read a text file from the workspace with line numbers. Optional start_line and end_line for large files. For images use view_image instead.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative or absolute path to the file." },
                            ["start_line"] = new JsonObject { ["type"] = "integer", ["description"] = "1-based start line (optional)." },
                            ["end_line"] = new JsonObject { ["type"] = "integer", ["description"] = "1-based end line (optional)." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "view_image",
                    ["description"] = "View an image in the workspace (png, jpg, jpeg, gif, webp, bmp, ico) so you can actually see its contents. Use this instead of read_file for images.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative or absolute path to the image file." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write_file",
                    ["description"] = "Create a new file or overwrite an existing file with complete content. Automatically creates an undo checkpoint.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path to the file." },
                            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Complete content to write." }
                        },
                        ["required"] = new JsonArray { "path", "content" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "patch_file",
                    ["description"] = "Surgically replace a block of text inside an existing file (supports exact and whitespace-tolerant matching). Automatically creates an undo checkpoint.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path to the file." },
                            ["search"] = new JsonObject { ["type"] = "string", ["description"] = "Existing code block to find." },
                            ["replace"] = new JsonObject { ["type"] = "string", ["description"] = "New replacement code block." }
                        },
                        ["required"] = new JsonArray { "path", "search", "replace" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "delete_file",
                    ["description"] = "Delete a file from the workspace after creating a reversible undo checkpoint.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path to the file to delete." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "move_file",
                    ["description"] = "Move or rename a file or directory within the workspace.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["source_path"] = new JsonObject { ["type"] = "string", ["description"] = "Existing relative path." },
                            ["destination_path"] = new JsonObject { ["type"] = "string", ["description"] = "New relative path." }
                        },
                        ["required"] = new JsonArray { "source_path", "destination_path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "list_dir",
                    ["description"] = "List files and subdirectories in a directory with file sizes.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path to directory (empty for workspace root)." }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "search_code",
                    ["description"] = "Search across all code files in the workspace using a regex or text pattern. Returns matching file paths, line numbers, and lines.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Regex or text pattern to search for." },
                            ["file_filter"] = new JsonObject { ["type"] = "string", ["description"] = "Optional filename/extension filter, e.g. '.cpp' or '.cs'." }
                        },
                        ["required"] = new JsonArray { "pattern" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "search_codebase",
                    ["description"] = "Semantic & symbol-aware codebase index query. Returns the most relevant functions, classes, and code chunks across the entire workspace for natural language concepts or symbol names.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Concept, symbol, or question to search in the codebase (e.g. 'authentication', 'terminal pty', 'handleHover')." }
                        },
                        ["required"] = new JsonArray { "query" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "run_command",
                    ["description"] = "Execute a PowerShell command in the workspace and stream output.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "PowerShell command line to execute." }
                        },
                        ["required"] = new JsonArray { "command" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "git_status_diff",
                    ["description"] = "Inspect Git repository branch status and working tree diff statistics.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Optional specific file path to diff." }
                        }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_diagnostics",
                    ["description"] = "Run compiler/typechecker diagnostics (dotnet build, cargo check, tsc) on the workspace and return warnings/errors.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject()
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "inspect_binary",
                    ["description"] = "Inspect a binary/executable file (.exe, .dll, .bin): extracts PE32/PE32+ headers, sections, Shannon entropy, SHA-256/MD5 hashes, header hex dump, and ASCII + UTF-16LE strings.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the binary file." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "web_fetch",
                    ["description"] = "Fetch text content from an HTTP/HTTPS URL (for reading docs, APIs, or web pages).",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["url"] = new JsonObject { ["type"] = "string", ["description"] = "Full URL to fetch." }
                        },
                        ["required"] = new JsonArray { "url" }
                    }
                }
            }
        };

        if (!planOnly) return defs;

        // In plan mode the model only ever sees read-only tools.
        var filtered = new JsonArray();
        foreach (var node in defs)
        {
            var name = node?["function"]?["name"]?.GetValue<string>();
            if (name != null && PlanModeTools.Contains(name))
                filtered.Add(node?.DeepClone());
        }
        return filtered;
    }
}
