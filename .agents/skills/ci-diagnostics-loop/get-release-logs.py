#!/usr/bin/env python3
"""
Fetch GitHub Actions workflow logs for the latest Release run.
Public API, no token needed (but rate limited to 60 req/hour).
Use GitHub Personal Access Token (PAT) for 5000 req/hour limit.

To use a PAT:
  1. Go to https://github.com/settings/tokens
  2. Click "Generate new token (classic)"
  3. Select scopes: repo, read:org, read:user, read:repo_hook
  4. Copy the token
  5. Export to environment: $env:GITHUB_PAT = "<your-token>"
  6. Run this script
"""

import requests
import json
import sys
import os
import time
from datetime import datetime

REPO = "lyh543/ham-gallery"
MAX_RETRIES = 3
RETRY_DELAY = 10  # seconds

def get_headers():
    """Build request headers with optional PAT."""
    headers = {}
    # Support multiple token variable names
    pat = (os.environ.get("GITHUB_PAT") or 
           os.environ.get("GH_TOKEN") or 
           os.environ.get("HAM_GALLERY_READ_ONLY_PAT"))
    if pat:
        headers["Authorization"] = f"token {pat}"
    return headers

def get_latest_release_runs(limit=5):
    """Get the latest Release workflow runs with retry logic."""
    url = f"https://api.github.com/repos/{REPO}/actions/runs"
    params = {
        "per_page": limit,
        "status": "completed"
    }
    headers = get_headers()
    
    for attempt in range(MAX_RETRIES):
        try:
            resp = requests.get(url, params=params, headers=headers)
            if resp.status_code == 403:
                # Rate limited or forbidden
                print(f"\n❌ GitHub API rate limited (403 Forbidden)", file=sys.stderr)
                if "rate limit" in resp.text.lower() or "API rate limit exceeded" in resp.text:
                    print("\n📝 To fix this, provide a GitHub Personal Access Token:\n", file=sys.stderr)
                    print("  1. Go to: https://github.com/settings/tokens", file=sys.stderr)
                    print("  2. Click 'Generate new token (classic)'", file=sys.stderr)
                    print("  3. Select scopes: repo, read:org, read:user, read:repo_hook", file=sys.stderr)
                    print("  4. Copy the token and run:", file=sys.stderr)
                    print("     $env:GITHUB_PAT = '<your-token>'", file=sys.stderr)
                    print("  5. Then re-run this script\n", file=sys.stderr)
                sys.exit(1)
            
            resp.raise_for_status()
            break
        except requests.exceptions.RequestException as e:
            if attempt < MAX_RETRIES - 1:
                print(f"Request failed: {e}. Retrying in {RETRY_DELAY} seconds... (attempt {attempt + 1}/{MAX_RETRIES})", file=sys.stderr)
                time.sleep(RETRY_DELAY)
            else:
                raise
    
    data = resp.json()
    release_runs = [
        run for run in data.get("workflow_runs", [])
        if run["name"] == "Release"
    ]
    
    return release_runs

def get_jobs_for_run(run_id):
    """Get all jobs for a workflow run."""
    url = f"https://api.github.com/repos/{REPO}/actions/runs/{run_id}/jobs"
    headers = get_headers()
    
    resp = requests.get(url, headers=headers)
    resp.raise_for_status()
    
    data = resp.json()
    return data.get("jobs", [])

def get_run_logs(run_id):
    """Try to get logs for a run (may be restricted for private)."""
    url = f"https://api.github.com/repos/{REPO}/actions/runs/{run_id}/attempts/1/logs"
    headers = get_headers()
    
    resp = requests.get(url, headers=headers, allow_redirects=False)
    
    if resp.status_code == 302:
        # Redirect to actual log download
        redirect_url = resp.headers.get("Location")
        if redirect_url:
            resp = requests.get(redirect_url)
            return resp.text
    
    return None

def main():
    print("Fetching latest Release runs...\n", file=sys.stderr)
    
    runs = get_latest_release_runs(10)
    
    if not runs:
        print("No Release runs found", file=sys.stderr)
        return
    
    # Show the most recent ones
    for run in runs[:3]:
        run_id = run["id"]
        created = run["created_at"]
        conclusion = run["conclusion"]
        status = run["status"]
        ref = run.get("head_branch", "unknown")
        commit = run["head_commit"]["id"][:7] if run.get("head_commit") else "unknown"
        
        print("=" * 80)
        print(f"Run ID: {run_id}")
        print(f"Ref: {ref} | Commit: {commit}")
        print(f"Created: {created}")
        print(f"Status: {status} | Conclusion: {conclusion}")
        print(f"URL: {run['html_url']}")
        print("=" * 80)
        
        # Get jobs for this run
        jobs = get_jobs_for_run(run_id)
        print(f"\nJobs ({len(jobs)}):")
        
        failed_jobs = []
        for job in jobs:
            status = job["status"]
            conclusion = job["conclusion"]
            name = job["name"]
            
            icon = "✓" if conclusion == "success" else "✗" if conclusion == "failure" else "○"
            print(f"  {icon} {name:50} {conclusion}")
            
            if conclusion == "failure":
                failed_jobs.append(job)
        
        if failed_jobs:
            print(f"\nFailed jobs details:")
            for job in failed_jobs:
                print(f"\n  Job: {job['name']}")
                print(f"  URL: {job['html_url']}")
                print(f"  Steps:")
                # Note: steps detail requires private API access
                for step in job.get("steps", []):
                    step_conclusion = step.get("conclusion", "unknown")
                    step_name = step.get("name", "unknown")
                    if step_conclusion == "failure":
                        print(f"    ✗ {step_name}")
        
        print()

if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
