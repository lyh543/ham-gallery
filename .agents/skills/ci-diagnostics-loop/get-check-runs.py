#!/usr/bin/env python3
"""
Fetch check runs within a suite and their detailed logs.
"""

import requests
import sys
import os
import io

# Force UTF-8 output
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

REPO = "lyh543/ham-gallery"

def get_headers():
    """Build request headers with PAT."""
    headers = {"Accept": "application/vnd.github+json"}
    pat = (os.environ.get("GITHUB_PAT") or 
           os.environ.get("GH_TOKEN") or 
           os.environ.get("HAM_GALLERY_READ_ONLY_PAT"))
    if pat:
        headers["Authorization"] = f"token {pat}"
    return headers

def get_check_runs_for_suite(suite_id):
    """Fetch check runs within a suite."""
    # Note: Check suite API in GraphQL or REST might not directly give us step logs
    # The logs might need to be fetched from the Actions API instead
    # This is a workaround - check runs don't have direct step logs in REST API
    
    # Alternative: use the actual Actions job ID to get logs
    print(f"Suite ID {suite_id} identified.", file=sys.stderr)
    print("\nNote: GitHub's REST API doesn't expose check run step logs directly.", file=sys.stderr)
    print("To view logs, use GitHub Actions UI or the job logs API.", file=sys.stderr)
    print(f"\nSuite logs URL: https://github.com/{REPO}/suites/{suite_id}/logs?attempt=1", file=sys.stderr)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python get-check-runs.py <suite_id>", file=sys.stderr)
        print("Example: python get-check-runs.py 67739213549", file=sys.stderr)
        sys.exit(1)
    
    suite_id = sys.argv[1]
    get_check_runs_for_suite(suite_id)
