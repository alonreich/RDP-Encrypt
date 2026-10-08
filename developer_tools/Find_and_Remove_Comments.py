import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO CACHE. BOTH LINES, BEFORE EVERY OTHER IMPORT.
#
# ⚠️ THE ENV VAR IS NOT OPTIONAL IN THIS FILE — IT IS THE ONLY ONE THAT WORKS HERE.
# This script uses multiprocessing.Pool. On Windows that SPAWNS fresh Python processes which
# re-import this module from scratch, and a child process does NOT inherit the parent's
# `sys.dont_write_bytecode` — it reads PYTHONDONTWRITEBYTECODE from the environment. With only
# the sys flag set, every worker process was free to write a __pycache__.
# `dont_write_bytecode` also has to sit ABOVE the imports: it governs imports made after it.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import re
import ctypes
import shutil
import argparse
from pathlib import Path
from multiprocessing import Pool, cpu_count

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

WORKING_DIRECTORY = Path(__file__).resolve().parent.parent
TOOL_OUTPUT_DIRECTORY = get_downloads_directory() / "rdp_vault" / "developer_tools"
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = str(TOOL_OUTPUT_DIRECTORY / "pycache")
sys.pycache_prefix = os.environ['PYTHONPYCACHEPREFIX']

try:
    os.chdir(WORKING_DIRECTORY)
except Exception as e:
    print(f"Failed to change working directory: {e}")
    sys.exit(1)

RED = '\033[48;5;52m\033[97;1m'
GREEN = '\033[48;5;22m\033[97m'
CYAN = '\033[96m'
YELLOW = '\033[93m'
RESET = '\033[0m'

def center_console():
    try:
        try: ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except: ctypes.windll.user32.SetProcessDPIAware()
        user32 = ctypes.windll.user32
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if not hwnd: return
        rect = ctypes.Structure()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
    except: pass

EXCLUDE_FOLDERS = [
    '.git', 'bin', 'obj', '.vs', '.idea', 'packages', 'compile', 'compiled',
    'publish', 'artifacts', 'old_code', 'testresults', 'Claude outputs', 'AvaloniaPort'
]
EXCLUDE_FILES = ['AssemblyInfo.cs']
EXCLUDE_EXTS = [
    '.txt', '.log', '.json', '.resx', '.ico', '.png', '.gif', '.traineddata',
    '.dll', '.exe', '.config', '.manifest', '.xml', '.xsd', '.sln', '.DotSettings',
    '.props', '.targets', '.keystore'
]

CS_REGEX = re.compile(r'^(\s*)(?:(?:public|private|protected|internal|static|virtual|override|async|sealed|partial|abstract|readonly|record|unsafe|new)\s+)*(class|struct|interface|enum|delegate|void|int|string|bool|var|Task|auto)\s+([a-zA-Z0-9_<>]+)\s*[\(\{:]?.*$')

def get_target_files(root_dir):
    targets = []
    for root, dirs, files in os.walk(root_dir):
        dirs[:] = [d for d in dirs if d.lower() not in [ef.lower() for ef in EXCLUDE_FOLDERS]]
        for file in sorted(files, key=str.lower):
            if file in EXCLUDE_FILES: continue
            _, ext = os.path.splitext(file)
            if ext in EXCLUDE_EXTS: continue
            if ext == '.cs':
                targets.append(os.path.join(root, file))
    return targets

def display_path(filepath):
    try:
        return str(Path(filepath).resolve().relative_to(WORKING_DIRECTORY))
    except Exception:
        return str(filepath)

def analyze_comments(filepath):
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
    except Exception:
        return []

    actions = {}
    in_block = False
    in_verbatim = False

    for i, line in enumerate(lines):
        nl = line[len(line.rstrip('\r\n')):]
        if not nl:
            nl = '\n'
        stripped = line.strip()

        if in_block:
            end_idx = line.find('*/')
            if end_idx == -1:
                actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                continue
            else:
                in_block = False
                after = line[end_idx + 2:]
                if not after.strip():
                    actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                    continue
                else:
                    cmt_text = line[:end_idx + 2].strip()
                    actions[i] = {'action': 'EDIT', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': f'Rem: {cmt_text}', 'new_content': after.lstrip() + nl}
                    line = after

        n = len(line)
        idx = 0
        new_line_parts = []

        while idx < n:
            ch = line[idx]

            # In verbatim string
            if in_verbatim:
                if ch == '"':
                    if idx + 1 < n and line[idx + 1] == '"':
                        new_line_parts.append('""')
                        idx += 2
                        continue
                    else:
                        in_verbatim = False
                        new_line_parts.append('"')
                        idx += 1
                        continue
                else:
                    new_line_parts.append(ch)
                    idx += 1
                    continue

            # Check verbatim string start (@" or $@" or @$")
            if ch == '@' and idx + 1 < n and line[idx + 1] == '"':
                new_line_parts.append('@"')
                idx += 2
                in_verbatim = True
                continue
            if ch == '$' and idx + 2 < n and line[idx:idx + 3] in ('$@"', '@$"'):
                new_line_parts.append(line[idx:idx + 3])
                idx += 3
                in_verbatim = True
                continue
            if ch == '@' and idx + 2 < n and line[idx:idx + 3] == '@$"':
                new_line_parts.append('@$"')
                idx += 3
                in_verbatim = True
                continue

            # Check regular or interpolated string
            if ch == '"' or (ch == '$' and idx + 1 < n and line[idx + 1] == '"'):
                if ch == '$':
                    new_line_parts.append('$"')
                    idx += 2
                else:
                    new_line_parts.append('"')
                    idx += 1

                while idx < n:
                    c = line[idx]
                    if c == '\\':
                        new_line_parts.append(line[idx:idx + 2])
                        idx += 2
                        continue
                    if c == '"':
                        new_line_parts.append('"')
                        idx += 1
                        break
                    new_line_parts.append(c)
                    idx += 1
                continue

            # Check character literal
            if ch == "'":
                new_line_parts.append("'")
                idx += 1
                while idx < n:
                    c = line[idx]
                    if c == '\\':
                        new_line_parts.append(line[idx:idx + 2])
                        idx += 2
                        continue
                    if c == "'":
                        new_line_parts.append("'")
                        idx += 1
                        break
                    new_line_parts.append(c)
                    idx += 1
                continue

            # Check single line comment (// or ///)
            if ch == '/' and idx + 1 < n and line[idx + 1] == '/':
                cmt = line[idx:]
                before = ''.join(new_line_parts)
                is_doc = cmt.strip().startswith('///')
                cmt_type = 'DOC COMMENT' if is_doc else 'COMMENT'
                if not before.strip():
                    actions[i] = {'action': 'DELETE', 'type': cmt_type, 'line': i + 1, 'content': stripped}
                else:
                    actions[i] = {'action': 'EDIT', 'type': 'INLINE COMMENT', 'line': i + 1, 'content': f'Rem: {cmt.strip()}', 'new_content': before.rstrip() + nl}
                break

            # Check block comment (/* ... */)
            if ch == '/' and idx + 1 < n and line[idx + 1] == '*':
                close_idx = line.find('*/', idx + 2)
                before = ''.join(new_line_parts)
                if close_idx != -1:
                    cmt = line[idx:close_idx + 2]
                    after = line[close_idx + 2:]
                    if not before.strip() and not after.strip():
                        actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                        break
                    else:
                        spacing = ' ' if before and not before.endswith((' ', '\t', '{', '(')) and after and not after.startswith((' ', '\t', '}', ')')) else ''
                        new_content = before.rstrip() + spacing + after.lstrip()
                        if not new_content.endswith(nl):
                            new_content = new_content.rstrip() + nl
                        actions[i] = {'action': 'EDIT', 'type': 'INLINE BLOCK COMMENT', 'line': i + 1, 'content': f'Rem: {cmt.strip()}', 'new_content': new_content}
                        break
                else:
                    in_block = True
                    cmt = line[idx:]
                    if not before.strip():
                        actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                    else:
                        actions[i] = {'action': 'EDIT', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': f'Rem: {cmt.strip()}', 'new_content': before.rstrip() + nl}
                    break

            new_line_parts.append(ch)
            idx += 1

    # Check excessive empty lines (>= 3 consecutive empty lines)
    empty_count = 0
    for i, line in enumerate(lines):
        if i in actions:
            empty_count = 0
            continue
        if not line.strip():
            empty_count += 1
            if empty_count >= 3:
                actions[i] = {'action': 'DELETE', 'type': 'EXCESSIVE EMPTY', 'line': i + 1, 'content': '<Excessive Empty>'}
        else:
            empty_count = 0

    return [v for k, v in sorted(actions.items(), key=lambda x: x[0])]

def nuke_comments(filepath, items):
    try:
        print(f"\n{CYAN}Executing Cleanup: {display_path(filepath)}{RESET}")
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
        action_map = {item['line'] - 1: item for item in items}
        with open(filepath, 'w', encoding='utf-8-sig') as f:
            for i, line in enumerate(lines):
                if i in action_map:
                    act = action_map[i]
                    print("-" * 60)
                    print(f"Line {act['line']}: {act['type']}")
                    print(f"{RED}- {line.rstrip()}{RESET}")
                    if act['action'] == 'EDIT':
                        print(f"{GREEN}+ {act['new_content'].rstrip()}{RESET}")
                        f.write(act['new_content'])
                else:
                    f.write(line)
        return True
    except Exception as e:
        print(f"Error: {e}")
        return False

def check_syntax(filepath):
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            source = f.read()
        if '\t' in source: return "Contains Tabs"
        if source.count('{') != source.count('}'): return "Mismatched Braces"
        return None
    except: return None

def analyze_duplicates(filepath):
    found = {}
    duplicates = []
    current_ns, current_cls = "GLOBAL", "GLOBAL"
    try:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            for i, line in enumerate(f, 1):
                stripped = line.strip()
                if not stripped or stripped.startswith(('/')): continue
                if stripped.startswith('namespace '):
                    current_ns = stripped.split()[1]
                    continue
                match = CS_REGEX.match(line)
                if match:
                    indent, kw, sig = match.groups()
                    if kw in ['class', 'struct', 'interface']: current_cls = sig
                    key = (current_ns, current_cls, kw, sig)
                    if key not in found: found[key] = []
                    found[key].append(i)
        for (ns, cls, kw, sig), lines in found.items():
            if len(lines) > 1:
                duplicates.append([os.path.basename(filepath), f"{ns}.{cls}", kw, sig, ", ".join(map(str, lines))])
    except: pass
    return duplicates

def print_table(title, data, headers):
    if not data:
        print(f"\n{title}: No issues found.")
        return
    print(f"\n{title}")
    widths = [len(h) for h in headers]
    for row in data:
        for i, val in enumerate(row):
            widths[i] = max(widths[i], len(str(val)))
    widths = [w + 2 for w in widths]
    h_str = " | ".join(f"{h:^{w}}" for h, w in zip(headers, widths))
    print("-" * len(h_str))
    print(h_str)
    print("-" * len(h_str))
    for row in data:
        print(" | ".join(f"{str(val):<{w}}" for val, w in zip(row, widths)))
    print("-" * len(h_str))

def purge_bytecode_cache():
    """
    Removes any __pycache__ / .pyc this tool's folder has accumulated.
    Scoped to developer_tools ONLY. It must never wander into the rest of the repository.
    """
    tools_dir = Path(__file__).resolve().parent
    removed = []
    try:
        for cache_dir in tools_dir.rglob("__pycache__"):
            if cache_dir.is_dir():
                shutil.rmtree(cache_dir, ignore_errors=True)
                if not cache_dir.exists():
                    removed.append(cache_dir.name)
        for stray in list(tools_dir.rglob("*.pyc")) + list(tools_dir.rglob("*.pyo")):
            try:
                stray.unlink()
                removed.append(stray.name)
            except Exception:
                pass
    except Exception as exc:
        print(f"[!] Could not sweep bytecode cache: {exc}")
        return
    if removed:
        print(f"[*] Removed {len(removed)} bytecode cache item(s).")

def main():
    parser = argparse.ArgumentParser(description="RDP Vault C# Advanced Code Cleaner & Comment Stripper")
    parser.add_argument("--report-only", "--dry-run", action="store_true", help="Report comments and syntax issues without prompting to modify files")
    parser.add_argument("-y", "--yes", action="store_true", help="Automatically approve comment removal without interactive prompt")
    args = parser.parse_args()

    purge_bytecode_cache()
    try:
        os.system('title RDP Vault C# Advanced Code Cleaner')
    except Exception:
        pass

    print(f"{CYAN}--- RDP VAULT C# ADVANCED CODE CLEANER ---{RESET}")
    print(f"Target Directory: {WORKING_DIRECTORY}")
    
    files = get_target_files(WORKING_DIRECTORY)
    print(f"Analyzing {len(files)} C# files...")
    
    junk_data = []
    files_with_junk = {}
    
    for idx, f in enumerate(files):
        if idx % 10 == 0: print(f"  Scanning for junk... {idx}/{len(files)}", end='\r')
        items = analyze_comments(f)
        if items:
            files_with_junk[f] = items
            for item in items:
                preview = (item['content'][:50] + '..') if len(item['content']) > 50 else item['content']
                junk_data.append([display_path(f), item['line'], item['type'], preview])

    print("\n" + "=" * 80)
    print(f"{YELLOW}STEP 1: REVIEW COMMENTS & UNNECESSARY EMPTY LINES{RESET}")
    print("=" * 80)
    
    if junk_data:
        print_table("TABLE 1: IDENTIFIED JUNK (COMMENTS & EXCESSIVE EMPTY LINES)", junk_data, ["File", "Line", "Type", "Content Preview"])
        if args.report_only:
            print(f"\n{CYAN}[Report Only] Dry run specified. No files modified.{RESET}")
        else:
            print(f"\n{YELLOW}WARNING: This action will permanently remove all items listed above.{RESET}")
            if args.yes:
                q = 'Y'
                print(">>> Auto-approving cleanup via --yes flag.")
            else:
                try:
                    q = input(">>> Do you approve the removal of these comments/empty lines? (Y/N): ").strip().upper()
                except (EOFError, KeyboardInterrupt):
                    q = 'N'
            if q == 'Y':
                for f, items in files_with_junk.items():
                    nuke_comments(f, items)
                print(f"\n{GREEN}Cleanup complete.{RESET}")
            else:
                print(f"\n{CYAN}Cleanup cancelled by user.{RESET}")
    else:
        print("No comments or unnecessary empty lines found.")

    print("\n" + "=" * 80)
    print(f"{YELLOW}STEP 2: SYSTEM ANALYSIS (SYNTAX & DUPLICATES){RESET}")
    print("=" * 80)
    
    syntax_data = []
    for f in files:
        err = check_syntax(f)
        if err: syntax_data.append([display_path(f), err])
    print_table("TABLE 2: SYNTAX & INDENTATION WARNINGS", syntax_data, ["File", "Issue"])

    all_dupes = []
    with Pool(processes=cpu_count()) as pool:
        results = pool.map(analyze_duplicates, files)
    for res in results: all_dupes.extend(res)
    print_table("TABLE 3: SCOPE-AWARE DUPLICATES (REPORT ONLY)", all_dupes, ["File", "Scope", "Type", "Signature", "Lines"])

    purge_bytecode_cache()
    print(f"\n{CYAN}Done.{RESET}")

if __name__ == "__main__":
    main()
