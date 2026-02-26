using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Manages project-specific context files that are served to MCP agents.
    /// Context files live in Assets/MCP/Context/ (configurable) and are simple Markdown files
    /// that provide agents with project knowledge — guidelines, architecture, game design, etc.
    ///
    /// This is entirely optional: if no context files exist, agents work normally without context.
    /// </summary>
    public static class MCPContextManager
    {
        // Standard category file names (without .md extension)
        private static readonly string[] StandardCategories = new[]
        {
            "ProjectGuidelines",
            "Architecture",
            "GameDesign",
            "NetworkingGuidelines",
            "NetworkingCSP",
        };

        /// <summary>
        /// Get the absolute path to the context folder.
        /// </summary>
        public static string GetContextFolderPath()
        {
            string relative = MCPSettingsManager.ContextPath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative));
        }

        /// <summary>
        /// Check if the context folder exists and has any .md files.
        /// </summary>
        public static bool HasContextFiles()
        {
            string folder = GetContextFolderPath();
            if (!Directory.Exists(folder)) return false;
            return Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Length > 0;
        }

        /// <summary>
        /// Get a list of all detected context files with metadata.
        /// Returns category name → file info for UI display.
        /// </summary>
        public static List<ContextFileInfo> GetContextFileList()
        {
            var results = new List<ContextFileInfo>();
            string folder = GetContextFolderPath();

            if (!Directory.Exists(folder))
                return results;

            // Standard files
            foreach (var cat in StandardCategories)
            {
                string filePath = Path.Combine(folder, cat + ".md");
                results.Add(new ContextFileInfo
                {
                    Category = cat,
                    FilePath = filePath,
                    Exists = File.Exists(filePath),
                    IsStandard = true,
                    SizeBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                });
            }

            // Custom folder
            string customFolder = Path.Combine(folder, "Custom");
            if (Directory.Exists(customFolder))
            {
                foreach (var file in Directory.GetFiles(customFolder, "*.md"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    results.Add(new ContextFileInfo
                    {
                        Category = $"Custom/{name}",
                        FilePath = file,
                        Exists = true,
                        IsStandard = false,
                        SizeBytes = new FileInfo(file).Length,
                    });
                }
            }

            // Also pick up any non-standard .md files in root context folder
            foreach (var file in Directory.GetFiles(folder, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name == "README") continue; // Skip README
                if (StandardCategories.Contains(name)) continue; // Already listed
                results.Add(new ContextFileInfo
                {
                    Category = name,
                    FilePath = file,
                    Exists = true,
                    IsStandard = false,
                    SizeBytes = new FileInfo(file).Length,
                });
            }

            return results;
        }

        /// <summary>
        /// Read a specific context file by category name.
        /// Returns null if file doesn't exist.
        /// </summary>
        public static string GetContextByCategory(string category)
        {
            string folder = GetContextFolderPath();

            // Try direct match first
            string filePath = Path.Combine(folder, category + ".md");
            if (File.Exists(filePath))
                return File.ReadAllText(filePath);

            // Try in Custom subfolder
            filePath = Path.Combine(folder, "Custom", category + ".md");
            if (File.Exists(filePath))
                return File.ReadAllText(filePath);

            // Try with Custom/ prefix stripped
            if (category.StartsWith("Custom/"))
            {
                string name = category.Substring("Custom/".Length);
                filePath = Path.Combine(folder, "Custom", name + ".md");
                if (File.Exists(filePath))
                    return File.ReadAllText(filePath);
            }

            return null;
        }

        /// <summary>
        /// Read all context files. Returns category → content dictionary.
        /// Only returns files that exist and have content.
        /// </summary>
        public static Dictionary<string, string> GetAllContext()
        {
            var result = new Dictionary<string, string>();
            var files = GetContextFileList();

            foreach (var file in files)
            {
                if (!file.Exists || file.SizeBytes == 0) continue;

                try
                {
                    string content = File.ReadAllText(file.FilePath);
                    if (!string.IsNullOrWhiteSpace(content))
                        result[file.Category] = content;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AB-UMCP] Failed to read context file {file.Category}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Get all context as a serializable response for the HTTP API.
        /// </summary>
        public static object GetContextResponse(string category = null)
        {
            if (!MCPSettingsManager.ContextEnabled)
            {
                return new Dictionary<string, object>
                {
                    { "enabled", false },
                    { "message", "Project context is disabled. Enable it in Window > AB Unity MCP." },
                };
            }

            if (!string.IsNullOrEmpty(category))
            {
                string content = GetContextByCategory(category);
                if (content == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "error", $"Context category '{category}' not found." },
                        { "availableCategories", GetAvailableCategories() },
                    };
                }

                return new Dictionary<string, object>
                {
                    { "category", category },
                    { "content", content },
                };
            }

            // Return all context
            var allContext = GetAllContext();
            var entries = new List<Dictionary<string, object>>();

            foreach (var kvp in allContext)
            {
                entries.Add(new Dictionary<string, object>
                {
                    { "category", kvp.Key },
                    { "content", kvp.Value },
                });
            }

            return new Dictionary<string, object>
            {
                { "enabled", true },
                { "contextPath", MCPSettingsManager.ContextPath },
                { "fileCount", entries.Count },
                { "categories", entries },
            };
        }

        /// <summary>
        /// Get a compact context summary suitable for auto-injection into first tool response.
        /// Returns null if no context is available.
        /// </summary>
        public static string GetContextSummary()
        {
            if (!MCPSettingsManager.ContextEnabled) return null;

            var allContext = GetAllContext();
            if (allContext.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== PROJECT CONTEXT (auto-provided by AB Unity MCP) ===");
            sb.AppendLine();

            foreach (var kvp in allContext)
            {
                sb.AppendLine($"--- {kvp.Key} ---");
                // Truncate very long files to keep auto-inject reasonable
                string content = kvp.Value;
                if (content.Length > 2000)
                    content = content.Substring(0, 2000) + "\n... [truncated — use unity_get_project_context for full content]";
                sb.AppendLine(content);
                sb.AppendLine();
            }

            sb.AppendLine("=== END PROJECT CONTEXT ===");
            return sb.ToString();
        }

        /// <summary>
        /// Get list of available category names.
        /// </summary>
        public static List<string> GetAvailableCategories()
        {
            var categories = new List<string>();
            var files = GetContextFileList();
            foreach (var file in files)
            {
                if (file.Exists && file.SizeBytes > 0)
                    categories.Add(file.Category);
            }
            return categories;
        }

        /// <summary>
        /// Create default template files in the context folder.
        /// Only creates files that don't already exist.
        /// </summary>
        public static int CreateDefaultTemplates()
        {
            string folder = GetContextFolderPath();
            string customFolder = Path.Combine(folder, "Custom");

            // Ensure directories exist
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(customFolder);

            int created = 0;

            // README
            string readmePath = Path.Combine(folder, "README.md");
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath, GetReadmeTemplate());
                created++;
            }

            // Standard templates
            var templates = new Dictionary<string, string>
            {
                { "ProjectGuidelines", GetProjectGuidelinesTemplate() },
                { "Architecture", GetArchitectureTemplate() },
                { "GameDesign", GetGameDesignTemplate() },
                { "NetworkingGuidelines", GetNetworkingGuidelinesTemplate() },
                { "NetworkingCSP", GetNetworkingCSPTemplate() },
            };

            foreach (var kvp in templates)
            {
                string filePath = Path.Combine(folder, kvp.Key + ".md");
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, kvp.Value);
                    created++;
                }
            }

            AssetDatabase.Refresh();
            return created;
        }

        // ─── Template Content ───

        private static string GetReadmeTemplate() => @"# AB Unity MCP — Project Context

This folder contains project-specific context files that are automatically
provided to AI agents when they connect via the AB Unity MCP plugin.

## How It Works

- Place `.md` files in this folder with project documentation
- Agents receive this context automatically when they connect
- Use standard filenames for recognized categories, or add custom files

## Standard Categories

| File | Purpose |
|------|---------|
| `ProjectGuidelines.md` | Coding standards, naming conventions, workflow rules |
| `Architecture.md` | System architecture, module map, tech stack |
| `GameDesign.md` | Core gameplay, mechanics, progression, balancing |
| `NetworkingGuidelines.md` | Networking architecture, sync strategy, protocols |
| `NetworkingCSP.md` | Content Security Policy, allowed endpoints, security rules |

## Custom Context

Add any additional `.md` files to the `Custom/` subfolder, or directly in this folder.
All `.md` files will be discovered and served to agents.

## Tips

- Keep files concise — agents work best with focused, relevant context
- Use clear headings and bullet points for easy parsing
- Update files as your project evolves
- This folder is designed to be version-controlled with your project
";

        private static string GetProjectGuidelinesTemplate() => @"# Project Guidelines

<!-- Fill in your project's coding standards and conventions below -->
<!-- Delete sections that don't apply, add your own -->

## Naming Conventions

- Classes: PascalCase
- Methods: PascalCase
- Private fields: _camelCase with underscore prefix
- Constants: UPPER_SNAKE_CASE or PascalCase
- Scene objects: PascalCase with descriptive names

## Code Style

- Use explicit access modifiers (private, public, etc.)
- Prefer [SerializeField] private over public fields
- Add XML documentation comments on public APIs
- Keep methods under 30 lines where possible

## Project Structure

- `Assets/Scripts/` — All gameplay scripts
- `Assets/Prefabs/` — Prefab assets
- `Assets/Scenes/` — Scene files
- `Assets/Art/` — Visual assets
- `Assets/Audio/` — Sound effects and music

## Git Workflow

- Feature branches from `develop`
- PR required for merge to `main`
- Commit messages: `type: description` (feat, fix, refactor, etc.)

## Unity Version

- Target Unity version: <!-- e.g., 2022.3 LTS -->
- Render pipeline: <!-- URP / HDRP / Built-in -->
";

        private static string GetArchitectureTemplate() => @"# Project Architecture

<!-- Describe your project's technical architecture below -->

## System Overview

<!-- High-level description of your game's architecture -->

## Core Systems

<!-- List and describe major systems -->
<!-- Example:
### Player Controller
- Input handling via New Input System
- State machine for movement states
- Physics-based movement with Rigidbody

### Game Manager
- Singleton pattern
- Manages game state (menu, playing, paused, game over)
- Scene loading and transitions
-->

## Data Flow

<!-- Describe how data flows through your systems -->

## Dependencies

<!-- External packages, plugins, SDKs used -->

## Build Targets

<!-- Platforms you're targeting and any platform-specific considerations -->
";

        private static string GetGameDesignTemplate() => @"# Game Design Document

<!-- Describe your game's design below -->

## Game Overview

- **Genre:** <!-- e.g., Action RPG, Puzzle Platformer -->
- **Target Audience:** <!-- e.g., Casual, Hardcore, All ages -->
- **Platform:** <!-- e.g., PC, Mobile, Console -->

## Core Gameplay

<!-- Describe the main gameplay loop -->

## Mechanics

<!-- List and describe key game mechanics -->

## Progression

<!-- How does the player progress? Levels, upgrades, story, etc. -->

## UI/UX Guidelines

<!-- UI style, interaction patterns, accessibility considerations -->
";

        private static string GetNetworkingGuidelinesTemplate() => @"# Networking Guidelines

<!-- Describe your networking architecture below -->
<!-- Delete this file if your project doesn't use networking -->

## Network Architecture

- **Type:** <!-- Client-Server / P2P / Hybrid -->
- **Framework:** <!-- Netcode for GameObjects / Mirror / Photon / Fish-Net / Custom -->
- **Transport:** <!-- UDP / TCP / WebSocket / Custom -->

## Synchronization Strategy

<!-- What is synced and how? -->
<!-- Example:
- Player position: Server-authoritative with client prediction
- Player actions: Client-authoritative with server validation
- Game state: Server-authoritative, broadcast to all clients
-->

## Authority Model

<!-- Who owns what? Server authority vs client authority per system -->

## Bandwidth Considerations

<!-- Update rates, compression, delta sync, etc. -->
";

        private static string GetNetworkingCSPTemplate() => @"# Networking Content Security Policy

<!-- Define your networking security rules below -->
<!-- Delete this file if not applicable -->

## Allowed Endpoints

<!-- List allowed server endpoints / domains -->

## Authentication

<!-- How are connections authenticated? -->

## Data Validation

<!-- Server-side validation rules for client data -->

## Anti-Cheat Considerations

<!-- What measures are in place? -->

## Rate Limiting

<!-- Request rate limits and throttling rules -->
";

        /// <summary>
        /// Info about a discovered context file.
        /// </summary>
        public struct ContextFileInfo
        {
            public string Category;
            public string FilePath;
            public bool Exists;
            public bool IsStandard;
            public long SizeBytes;
        }
    }
}
