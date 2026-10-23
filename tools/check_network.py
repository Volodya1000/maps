"""Check Linux strace connect/sendto output for non-loopback Internet calls."""
import argparse,ipaddress,json,re
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('trace',type=Path);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
external=[];addresses=[]
for line in a.trace.read_text().splitlines():
    if 'AF_INET' not in line:continue
    match=re.search(r'inet_addr\("([^"]+)"\)|inet_pton\(AF_INET6, "([^"]+)"',line)
    if not match:external.append(line);continue
    raw=match.group(1) or match.group(2);addresses.append(raw);address=ipaddress.ip_address(raw)
    if not address.is_loopback and not (getattr(address,'ipv4_mapped',None) and address.ipv4_mapped.is_loopback):external.append(line)
report=dict(scope='Linux runtime process and children only; not a Windows or Android device audit',destinations=addresses,non_loopback_calls=external)
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))
raise SystemExit(bool(external))
