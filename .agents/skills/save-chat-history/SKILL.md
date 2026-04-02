---
name: save-chat-history
description: "Organize a raw chat transcript markdown file into a cleaned Q&A markdown file. Use when chat compression caused context loss and the user provides a transcript file path."
argument-hint: "Path to transcript file, e.g. docs/llm-chat-history/20260402-2-implement-album-list.md"
---

# Save Chat History

Reads a raw transcript markdown file and writes an organized version in the same directory.
Uses chunked processing to avoid context-length limits.

## When to Use

- User asks to save or archive the current conversation
- User provides a transcript file that contains noisy logs (tool reads, terminal outputs, progress messages)
- You need a clean record that keeps only user questions and assistant answers
- Invoked via `/save-chat-history <transcript-file-path>`

## Procedure

### Step 1 - Read input and determine output path

1. Treat the skill argument as the source transcript file path.
2. Read the source file in chunks.
3. Build output path in the same directory using:
   - Input: `<name>.md`
   - Output: `<name>.organized.md`
4. If input does not end with `.md`, append `.organized.md` directly.

### Step 2 - Parse conversation turns (agent-agnostic)

Recognize turns by role markers only:

- User turn starts with `User:`
- Assistant turn starts with `Assistant:` or any assistant-role marker used in the transcript format

Collect full content of each turn until the next turn prefix.

Hard requirement:

- Do not depend on agent names (for example, do not hardcode a specific assistant brand name).
- Do not use vendor-specific pattern matching logic.

Chunking rule:

- One `User` turn + its following `Assistant` turn is one chunk.
- Read and process chunk-by-chunk instead of loading the full transcript at once.
- Maintain minimal state between chunks (current user prompt, current assistant response).

### Step 3 - Compress each chunk with subagents

Use multiple subagents to compress chunks in parallel:

1. Split transcript into chunk IDs in original order (`chunk-0001`, `chunk-0002`, ...).
2. Dispatch compression tasks to subagents; each subagent receives exactly one Q&A chunk.
3. Each subagent returns cleaned markdown for that chunk only.
4. Merge subagent results by chunk ID order to preserve chronology.

Ignore non-turn system/tool lines such as:

- `Read ...`
- `Ran terminal command: ...`
- `Created ...`
- `Replacing ...`
- `Completed ...`
- `Starting: ...`
- `Fetched ...`
- `Made changes.`
- raw command outputs and file listings

### Step 4 - Clean assistant content

For each assistant turn, keep only meaningful final response text.

Remove:

- internal progress narration (for example: "I will check...", "Now reading...")
- tool execution details and logs
- code-reading traces and search result dumps

Keep:

- final explanations and decisions
- implementation summary and key results
- code snippets that were part of the final answer

If an assistant turn has no meaningful content after cleaning, skip it.

### Step 5 - Stream output markdown

Output must be written in a streaming manner:

1. Initialize output with title and `Saved` line.
2. For each parsed chunk, append:
   - `## {user question}`
   - cleaned assistant answer
3. Continue appending until all chunks are processed.

This avoids context overflow on long transcripts.

### Step 6 - Write file and confirm

Write the file using this structure:

```
# {Short title describing the session topic}

> Saved: {YYYY-MM-DD}

{For each exchange in the conversation:}

## {User's question or request, verbatim or lightly paraphrased}

{Assistant's answer — keep the final written answer only.}
```

Requirement:

- User questions must be markdown level-2 headings (`## ...`).

Write to `<source>.organized.md` in the same directory.

If `<source>.organized.md` already exists, overwrite it directly.

Before confirmation, perform an end-of-file integrity check:

- Verify the last user chunk from source is present in output.
- If missing, continue reading remaining source and append missing chunks.
- Do not finish until EOF is consumed and last user chunk is included.

Confirm the file path to the user when done.
