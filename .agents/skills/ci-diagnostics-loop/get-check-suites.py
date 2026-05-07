#!/usr/bin/env python3
"""
Fetch check suites for a specific commit.
This gives us the suite IDs needed to access detailed logs.
"""

import requests
import sys
import os

REPO = "lyh543/ham-gallery"

def get_headers():
    """Build request headers with PAT."""
    headers = {"Accept": "application/vnd.github+json"}
    pat = (os.environ.get("GITHUB_PAT") or 
           os.environ.get("GH_TOKEN") or 
           os.environ.get("HAM_GALLERY_READ_ONLY_PAT"))
    if pat:
        headers["Authorization"] = f"token {pat}"
    else:
        print("⚠️  No PAT token found. Set $env:GITHUB_PAT or $env:HAM_GALLERY_READ_ONLY_PAT", file=sys.stderr)
    return headers

def get_check_suites(commit_sha):
    """Fetch check suites for a commit."""
    url = f"https://api.github.com/repos/{REPO}/commits/{commit_sha}/check-suites"
    headers = get_headers()
    
    print(f"Fetching check suites for commit {commit_sha}...\n", file=sys.stderr)
    
    try:
        resp = requests.get(url, headers=headers)
        resp.raise_for_status()
        
        data = resp.json()
        suites = data.get("check_suites", [])
        
        if not suites:
            print("No check suites found for this commit.", file=sys.stderr)
            return
        
        print(f"\nFound {len(suites)} check suite(s):\n")
        for suite in suites:
            suite_id = suite["id"]
            status = suite["status"]
            conclusion = suite["conclusion"]
            name = suite.get("app", {}).get("name", "unknown")
            
            print(f"  Suite ID: {suite_id}")
            print(f"  Status: {status}")
            print(f"  Conclusion: {conclusion}")
            print(f"  App: {name}")
            print(f"  Logs URL: https://github.com/{REPO}/suites/{suite_id}/logs?attempt=1")
            print()
        
    except requests.exceptions.RequestException as e:
        print(f"❌ Error: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python get-check-suites.py <commit_sha>", file=sys.stderr)
        print("Example: python get-check-suites.py 58379034d74d2903915caf4bc690405c62213b5a", file=sys.stderr)
        sys.exit(1)
    
    commit_sha = sys.argv[1]
    get_check_suites(commit_sha)
