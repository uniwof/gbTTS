import re,sys
def mini(src):
    out=[]
    for line in src.split('\n'):
        t=line.strip()
        if t.startswith('//'): continue
        line=re.sub(r'\s//\s.*$','',line) if '"' not in line else re.sub(r'(;|\{|\})\s*//.*$',r'\1',line)
        line=re.sub(r'\$?@?"(?:[^"\\]|\\.)*"', lambda m: '"<zh>"' if re.search(r'[\u4e00-\u9fff]',m.group(0)) else m.group(0), line)
        if line.strip()=='' : continue
        out.append(line.rstrip())
    return '\n'.join(out)
if __name__=='__main__':
    print(mini(open(sys.argv[1],encoding='utf-8').read()))
