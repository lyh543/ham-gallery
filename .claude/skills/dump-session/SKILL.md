---
name: dump-session
description: "Dump the current Claude Code session to a Markdown transcript file by finding the session JSONL in the projects directory via modification time. Use when the user wants to save or compress the current conversation."
argument-hint: "Output filename (without path), e.g. 20260407-heic-preload. Defaults to today's date + branch name."
---

# Dump Session

Reads the current session's JSONL file from the Claude projects directory and writes a raw Markdown transcript to `docs/llm-chat-history/`.

## When to Use

- User wants to save/archive the current conversation
- User wants to compress or summarize the current session
- Invoked via `/dump-session [filename]`

## Procedure

### Step 1 - Find the current session file

The projects directory for this repo is:
`C:\Users\lyh54\.claude\projects\c--Users-lyh54-git-github-ham-gallery\`

Run the following Node.js script to find the current session:

```js
const fs = require('fs');
const path = require('path');

const projDir = 'C:/Users/lyh54/.claude/projects/c--Users-lyh54-git-github-ham-gallery';
const files = fs.readdirSync(projDir).filter(f => f.endsWith('.jsonl'));

const withStats = files.map(f => ({
  file: f,
  mtime: fs.statSync(path.join(projDir, f)).mtime
})).sort((a, b) => b.mtime - a.mtime);

// The current session is the most recently modified file
const sessionFile = path.join(projDir, withStats[0].file);
console.log(sessionFile);
```

The most recently modified `.jsonl` file is the current session.

### Step 2 - Determine output filename

If the user provided a filename argument, use it. Otherwise, use `YYYYMMDD-<git-branch>`.

Get the current date: today's date from the system.
Get the current git branch by running: `git branch --show-current`

Output path: `docs/llm-chat-history/<filename>.md`

If the file already exists, overwrite it.

### Step 3 - Extract conversation from JSONL

Parse the JSONL file line by line. For each line:

- If `type === "user"`: extract text content from `message.content` array (items where `type === "text"`), concatenate them, strip `<ide_opened_file>...</ide_opened_file>`, `<ide_selection>...</ide_selection>`, and other XML-style tags that wrap IDE context. Keep the user's actual question text only.
- If `type === "assistant"`: extract text from `message.content` array (items where `type === "text"`). Skip items where `type === "thinking"`. Concatenate the text parts.
- Skip all other types (`file-history-snapshot`, `queue-operation`, `system`, `last-prompt`, `permission-mode`).

Also skip `parentUuid === null` check — include all user/assistant messages.

For assistant messages with tool use content (`type === "tool_use"` in the content array), format them as:
```
{tool name} {condensed input summary}
```
For tool results (`type === "tool_result"` in user messages), format them as:
```
{N} lines of output
```
or the actual content if short (< 5 lines).

### Step 4 - Write Markdown transcript

Format as a flat conversation log:

```markdown
User: {user message text}

{assistant message text}

User: {next user message}

{next assistant response}
```

- Each `User:` starts a new exchange.
- Assistant text follows immediately (no `Assistant:` prefix needed, matching existing transcript format).
- Separate exchanges with a blank line.

### Step 5 - Invoke save-chat-history

After writing the transcript file at `docs/llm-chat-history/<filename>.md`, immediately read `.agents/skills/save-chat-history/SKILL.md` and follow its instructions, using the transcript file path (e.g. `docs/llm-chat-history/<filename>.md`) as the argument.

Do not ask the user — proceed automatically.
