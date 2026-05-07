#!/usr/bin/env python3
"""
Fetch detailed logs for a specific GitHub Actions job using PAT.
Usage: python get-job-logs.py <job_id>
Example: python get-job-logs.py 74641464924
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
    else:
        print("⚠️  No PAT token found. Set $env:GITHUB_PAT or $env:HAM_GALLERY_READ_ONLY_PAT", file=sys.stderr)
    return headers

def get_job_logs(job_id):
    """Fetch logs for a specific job."""
    url = f"https://api.github.com/repos/{REPO}/actions/jobs/{job_id}/logs"
    headers = get_headers()
    
    print(f"Fetching logs for job {job_id}...\n", file=sys.stderr)
    
    try:
        resp = requests.get(url, headers=headers)
        
        if resp.status_code == 403:
            print(f"❌ Access denied (403). Check token permissions.", file=sys.stderr)
            print(f"Response: {resp.text[:200]}", file=sys.stderr)
            sys.exit(1)
        
        if resp.status_code == 404:
            print(f"❌ Job not found (404). Check job ID.", file=sys.stderr)
            sys.exit(1)
        
        resp.raise_for_status()
        
        # GitHub returns logs as plain text
        print(resp.text)
        
    except requests.exceptions.RequestException as e:
        print(f"❌ Error: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python get-job-logs.py <job_id>", file=sys.stderr)
        print("Example: python get-job-logs.py 74641464924", file=sys.stderr)
        sys.exit(1)
    
    job_id = sys.argv[1]
    get_job_logs(job_id)
