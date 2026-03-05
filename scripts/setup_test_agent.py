#!/usr/bin/env python3
import json

# Read existing config
with open('/home/szf/.openclaw/openclaw.json', 'r') as f:
    config = json.load(f)

# Add or update crawlph-test agent
agent_exists = False
for agent in config['agents'].get('list', []):
    if agent.get('id') == 'crawlph-test':
        agent_exists = True
        print("crawlph-test agent already exists")
        break

if not agent_exists:
    test_agent = {
        "id": "crawlph-test",
        "workspace": "/home/szf/repos/crawlph-test",
        "agentDir": "/home/szf/.openclaw/agents/crawlph-test",
        "model": "zai/glm-5",
        "sandbox": {"mode": "off"}
    }
    config['agents']['list'].append(test_agent)
    
    # Write back
    with open('/home/szf/.openclaw/openclaw.json', 'w') as f:
        json.dump(config, f, indent=2)
    
    print("✓ crawlph-test agent added successfully")

# Create data directories for test agent
import os
data_dirs = [
    '/home/szf/.openclaw/agents/crawlph-test',
    '/home/szf/.openclaw/agents/crawlph-test/data',
    '/home/szf/.openclaw/agents/crawlph-test/data/progress'
]
for dir_path in data_dirs:
    os.makedirs(dir_path, exist_ok=True)
    print(f"✓ Created: {dir_path}")
