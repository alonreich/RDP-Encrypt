import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO CACHE. BOTH LINES, BEFORE EVERY OTHER IMPORT.
# `dont_write_bytecode` must sit above imports because it only governs imports made after it.
# The environment variable ensures child/spawned processes inherit the bytecode lockout.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import shutil
from pathlib import Path

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

def is_binary(file_path: Path) -> bool:
    try:
        with open(file_path, 'rb') as f:
            chunk = f.read(2048)
            if b'\0' in chunk:
                return True
    except Exception:
        pass
    return False

def get_group_name(file_path: Path, project_root: Path) -> str:
    ext = file_path.suffix.lower()
    rel_path = str(file_path.relative_to(project_root)).lower()
    name = file_path.name.lower()

    # 00: Architecture, Specifications, and System Governance (Must be read first by AI)
    if name in ['project_structure.txt', 'readme.md', 'android_architecture.md', 'spec_governance.md', 'index.md'] or rel_path.startswith("docs\\") or rel_path.startswith("docs/"):
        return "00_Specifications_and_Architecture"

    # 01: Core Security, Hardware KeyStore & Cryptography
    if name in [
        'crypto.cs', 'androidhardwarekeystore.cs', 'vaultvalidation.cs',
        'recoverykeygenerator.cs', 'unlockthrottle.cs', 'securityenforcer.cs', 'windowshello.cs'
    ] or "security" in rel_path:
        if ext == '.cs':
            return "01_Security_and_Cryptography"

    # 02: Desktop Windows Application (Avalonia UI, MSTSC Orchestration, Trace Cleanup)
    if rel_path.startswith("rdpvault\\") or rel_path.startswith("rdpvault/"):
        if ext == '.cs':
            return "02_Desktop_Windows_App"

    # 03: Mobile Android Companion (Avalonia UI, Accessibility Auto-Type, Foreground Service)
    if rel_path.startswith("rdpvault.android\\") or rel_path.startswith("rdpvault.android/"):
        if ext == '.cs':
            return "03_Mobile_Android_App"

    # 04: UI Markup, Visual Trees & Manifests (Avalonia AXAML / XAML / XML)
    if ext in ['.axaml', '.xaml'] or name in ['androidmanifest.xml', 'app.manifest']:
        return "04_UI_Markup"

    # 05: Build, Packaging & System Configuration
    if ext in [
        '.json', '.json5', '.xml', '.csproj', '.sln', '.config', '.props',
        '.targets', '.ini', '.toml', '.ruleset', '.editorconfig'
    ] or name in [
        'build.cmd', 'build-apk.cmd', 'dev_build.cmd', 'installer.cmd', 'uninstaller.cmd',
        '.gitignore', '.gitattributes', '.agignore', '.clineignore', '.codexignore', '.geminiignore'
    ]:
        return "05_Build_and_Configuration"

    # 06: Developer Tools, Analyzers & Automation
    if "developer_tools" in rel_path:
        return "06_Developer_Tools_and_Automation"

    # 07: Assets & Binary Stubs (Metadata and file size tracking)
    if ext in [
        '.ico', '.png', '.jpg', '.jpeg', '.gif', '.svg', '.dll', '.exe',
        '.traineddata', '.keystore', '.bmp', '.webp', '.apk'
    ]:
        return "07_Assets_and_Binaries"

    return "08_Misc"

MAX_CODE_FILES = 8  # 8 code bundles + 1 manifest + 1 tree file = 10 files max upload limit
MIN_PART_CHARS = 400_000  # Avoid splitting small projects into tiny fragments unnecessarily

def pack_into_capped_parts(items, max_parts=8):
    """Packs (rel_path, block) tuples across at most max_parts bundles."""
    if not items:
        return []

    total_chars = sum(len(b) for _, b in items)
    target_limit = max(MIN_PART_CHARS, (total_chars // max_parts) + 1)

    parts = []
    current_items = []
    used = 0

    for rel_path, block in items:
        if current_items and (used + len(block) > target_limit) and (len(parts) < max_parts - 1):
            parts.append(current_items)
            current_items = [(rel_path, block)]
            used = len(block)
        else:
            current_items.append((rel_path, block))
            used += len(block)

    if current_items:
        parts.append(current_items)

    return parts

def generate_project_manifest(project_root: Path, groups: dict, total_files: int) -> str:
    divider = "=" * 80
    lines = [
        divider,
        "RDP VAULT — ARCHITECTURAL MANIFEST & SYSTEM CONTEXT",
        divider,
        f"Project Root:        {project_root}",
        "Target Frameworks:   .NET 9 (Windows: net9.0-windows10.0.19041.0 | Android: net9.0-android35.0)",
        "Architecture Style:  Avalonia UI Multi-Platform Security Vault (Windows Desktop + Android Mobile)",
        "Packaging Model:     Single-File Self-Contained WinExe (RDPVault.exe) & Signed APK (RDPVault.apk)",
        "",
        "PRIMARY ARCHITECTURAL INVARIANTS:",
        "  1. Zero Cleartext Credentials on Disk: Master key Argon2id (64 MiB, 3 passes, 4 lanes).",
        "     Profiles encrypted via AES-256-GCM (12-byte nonce, 16-byte tag). Passwords never touch",
        "     disk in cleartext and are passed to Windows Credential Manager as temporary session",
        "     credentials, deleted immediately upon Remote Desktop exit.",
        "  2. Hardware Key Binding: Windows Hello TPM signatures on Windows; Android Keystore /",
        "     StrongBox Keymaster backed by Class 3 Strong Biometrics (Fingerprint/3D Face) via",
        "     BiometricPrompt.CryptoObject on Android. Master keys cannot be extracted even with root.",
        "  3. Host Trace Cleansing: Immediate post-session removal of mstsc registry history,",
        "     Default.rdp, jump lists, recent items, and temporary credentials.",
        "  4. Desktop Topology Protection & 1080p Smart-Scroll: Mobile connections default strictly to",
        "     1920x1080 landscape with dynamic resolution=i:0 and smart sizing:i:1, preventing remote",
        "     Windows multi-monitor collapse or desktop icon scrambling.",
        "  5. Zero-Clipboard Mobile Credential Isolation: Mobile passwords never touch the Android",
        "     system clipboard or IME caches. Handled via scoped RdpAutoTypeService accessibility.",
        "  6. Perimeter Bypass & Stealth Knocking: Raw TCP SYN and HTTP GET port knocking precedes",
        "     Wake-on-LAN and RDP handoff to dynamically whitelist client IP on firewalls (MikroTik).",
        "",
        "SUBSYSTEM MAP & DIRECTORY HIERARCHY:",
        "  - project_structure.txt:            Source of truth, full architectural specifications and changelog.",
        "  - README.md:                         User documentation, quick-start, ADB setup and build instructions.",
        "  - ANDROID_ARCHITECTURE.md:           Android subsystem architecture and Keystore/Accessibility specs.",
        "  - RDPVault\\:                         Windows Desktop application (Avalonia UI, MSTSC launcher, trace cleaner).",
        "  - RDPVault.Android\\:                 Android companion application (Avalonia UI, Accessibility, StrongBox).",
        "  - RDPVault.Core\\:                    Shared cryptographic models and vault envelope definitions.",
        "  - developer_tools\\:                 Automation scripts, bytecode purgers, code cleaner, and AI exporters.",
        "  - compiled\\:                        Release binaries (RDPVault.exe and RDPVault.apk).",
        "",
        "EXPORT GROUP METRICS (Ordered by AI ingestion priority):",
    ]
    for group_name in sorted(groups):
        count = len(groups[group_name])
        lines.append(f"  [{group_name}] -> {count:>3} file(s)")
    lines.extend([
        "",
        f"Total Tracked Items: {total_files}",
        "AI INSTRUCTION: Inspect groups in order (00 -> 01 -> 02 -> ...). Enforce invariants above during review.",
        divider,
        ""
    ])
    return "\n".join(lines)

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / "rdp_vault"

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)

    # Clean up previous text exports to avoid orphaned files
    for old_file in download_dir.glob("*.txt"):
        old_file.unlink(missing_ok=True)

    # developer_tools is deliberately excluded from ignored_dirs so AI has visibility into automation & scripts
    ignored_dirs = {
        '.git', 'bin', 'obj', '.vs', '.idea', 'node_modules',
        'compile', 'compiled', 'publish', 'artifacts', 'packages', 'testresults',
        'venv', '.venv', 'env', '.pytest_cache', '__pycache__',
        'claude outputs', 'avaloniaport'
    }

    ignored_file_patterns = {
        'build.log', 'build-apk.log', 'rdpvault.log'
    }

    divider = "=" * 80

    # 1. Generate annotated directory tree with file sizes
    tree_file = download_dir / "00_file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        tf.write(f"Directory Tree of: {project_root}\n")
        tf.write(f"{divider}\n")
        try:
            for root, dirs, files in os.walk(project_root):
                dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
                rel = Path(root).relative_to(project_root)
                level = len(rel.parts) if rel.name else 0
                indent = ' ' * 4 * level
                folder_name = rel.name if rel.name else project_root.name
                tf.write(f"{indent}{folder_name}/\n")
                subindent = ' ' * 4 * (level + 1)
                for f in sorted(files, key=str.lower):
                    if f.lower() in ignored_file_patterns:
                        continue
                    fp = Path(root) / f
                    try:
                        sz = fp.stat().st_size
                        sz_str = f"{sz / (1024 * 1024):.1f} MB" if sz >= 1024 * 1024 else f"{sz / 1024:.1f} KB"
                        tf.write(f"{subindent}{f:<50} [{sz_str:>9}]\n")
                    except Exception:
                        tf.write(f"{subindent}{f}\n")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.bat', '.ps1', '.py', '.json', '.json5', '.xml',
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.ruleset',
        '.agignore', '.clineignore', '.codexignore', '.geminiignore'
    }

    binary_whitelist = {
        '.ico', '.png', '.jpg', '.jpeg', '.gif', '.mp3', '.mp4', '.wav',
        '.dll', '.exe', '.traineddata', '.keystore', '.bmp', '.webp', '.apk'
    }

    print(f"Aggregating grouped code into {download_dir}...")

    processed_count = 0
    groups = {}

    for root, dirs, files in os.walk(project_root):
        dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
        current_path = Path(root)

        for filename in sorted(files, key=str.lower):
            if filename.lower() in ignored_file_patterns:
                continue

            file_path = current_path / filename
            relative_path = file_path.relative_to(project_root)
            ext = file_path.suffix.lower()

            is_src = ext in source_whitelist or file_path.name.lower() in source_whitelist
            is_bin = ext in binary_whitelist

            if not is_src and not is_bin:
                continue

            if is_bin or is_binary(file_path):
                file_size = file_path.stat().st_size
                sz_str = f"{file_size / (1024 * 1024):.2f} MB" if file_size >= 1024 * 1024 else f"{file_size / 1024:.1f} KB"
                content_text = (
                    f"[BINARY / ASSET OMITTED FROM TEXT EXPORT]\n"
                    f"Relative Path: {relative_path}\n"
                    f"File Size:     {sz_str} ({file_size:,} bytes)\n"
                    f"Type:          {ext.upper() if ext else 'Binary'} Asset\n"
                    f"Note:          Full path indexed in 00_file_structure.txt. Kept on disk.\n\n"
                )
                line_count = 0
                size_kb = file_size / 1024.0
            else:
                try:
                    with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                        content_text = f.read()
                    if not content_text.endswith("\n"):
                        content_text += "\n"
                    content_text += "\n"
                except Exception as e:
                    content_text = f"[ERROR READING FILE: {e}]\n\n"
                line_count = content_text.count("\n")
                size_kb = len(content_text) / 1024.0

            header = f"{divider}\nFILE: {relative_path}  ({line_count:,} lines, {size_kb:,.1f} KB)\n{divider}\n"
            block = header + content_text
            group_name = get_group_name(file_path, project_root)
            groups.setdefault(group_name, []).append((relative_path, block))
            processed_count += 1

    # 2. Generate and write high-level manifest
    manifest_file = download_dir / "00_PROJECT_MANIFEST.txt"
    manifest_content = generate_project_manifest(project_root, groups, processed_count)
    with open(manifest_file, "w", encoding="utf-8") as mf:
        mf.write(manifest_content)

    # 3. Assemble all blocks following the strict group priority order (00 -> 01 -> 02 -> ...)
    all_blocks = []
    for group in sorted(groups):
        for item in groups[group]:
            all_blocks.append(item)

    parts = pack_into_capped_parts(all_blocks, max_parts=MAX_CODE_FILES)
    count = len(parts)

    report_rows = []
    if manifest_file.exists():
        report_rows.append((manifest_file.name, manifest_file.stat().st_size))
    if tree_file.exists():
        report_rows.append((tree_file.name, tree_file.stat().st_size))

    for i, part_items in enumerate(parts, start=1):
        part_name = f"code_bundle_part{i:02d}of{count:02d}.txt" if count > 1 else "code_bundle.txt"
        toc = "\n".join(f"  - {rel_path}" for rel_path, _ in part_items)
        banner = (
            f"{divider}\n"
            f"CODE EXPORT BUNDLE - PART {i} OF {count}\n"
            f"Context Manifest: 00_PROJECT_MANIFEST.txt\n"
            f"Directory Tree:   00_file_structure.txt\n"
            f"Files in this bundle ({len(part_items)}):\n"
            f"{toc}\n"
            f"{divider}\n\n"
        )
        part_body = "".join(block for _, block in part_items)
        full_payload = banner + part_body
        with open(download_dir / part_name, "w", encoding="utf-8", errors="replace") as out:
            out.write(full_payload)
        report_rows.append((part_name, len(full_payload.encode('utf-8'))))

    print()
    print("UPLOAD REPORT - upload 00_PROJECT_MANIFEST.txt, 00_file_structure.txt, and bundle parts together")
    print("-" * 80)
    grand_total = 0
    for name, byte_size in report_rows:
        grand_total += byte_size
        print(f"  {name:<38} {byte_size / 1024.0:>10,.1f} KB   ~{byte_size // 4:>9,} tokens")
    print("-" * 80)
    print(f"  TOTAL: {len(report_rows)} files (<= 10 limit respected), {grand_total / 1024.0:,.1f} KB")
    print()
    print(f"Done! Aggregated {processed_count} files into {len(parts)} bundle(s) + 1 manifest + 1 structure file.")

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

if __name__ == '__main__':
    purge_bytecode_cache()
    run_aggregator()
