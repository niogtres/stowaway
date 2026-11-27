"""Writes THIRD-PARTY-NOTICES.txt: the licenses of everything Stowaway's installer ships.

Run after changing dependencies (after `dotnet restore` and `npm ci`):

    python -I tools/generate-notices.py

Covers the NuGet packages of the service (from obj/project.assets.json), the npm packages in the web UI bundle
(production dependencies in web/package-lock.json), the self-contained .NET runtime, and the WiX custom actions
embedded in the MSI. Tools used at runtime but not distributed (restic, rclone in the runner image) are listed too.
"""
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "THIRD-PARTY-NOTICES.txt"
ASSETS = ROOT / "src" / "Stowaway.Service" / "obj" / "project.assets.json"
LOCK = ROOT / "web" / "package-lock.json"
LICENSE_FILES = re.compile(r"^(licen[cs]e|copying|notice)([.-].*)?$", re.IGNORECASE)

# Build-time-only packages: nothing from them is in the installed program.
NUGET_BUILD_ONLY = {"microsoft.net.illink.tasks", "microsoft.entityframeworkcore.analyzers"}

MIT_TEMPLATE = (ROOT / "LICENSE").read_text(encoding="utf-8").split("\n", 3)[3]  # the MIT text after our copyright line


def license_texts(folder: pathlib.Path) -> list[str]:
    texts = []
    for f in sorted(folder.iterdir()) if folder.is_dir() else []:
        if f.is_file() and LICENSE_FILES.match(f.name):
            try:
                texts.append(f.read_text(encoding="utf-8", errors="replace").strip())
            except OSError:
                pass
    return texts


def nuget_packages():
    assets = json.loads(ASSETS.read_text(encoding="utf-8"))
    target = next(k for k in assets["targets"] if k.endswith("win-x64"))
    packages_path = pathlib.Path(assets["project"]["restore"]["packagesPath"])
    for key, info in sorted(assets["targets"][target].items(), key=lambda kv: kv[0].lower()):
        if info.get("type") != "package":
            continue
        name, version = key.split("/")
        if name.lower() in NUGET_BUILD_ONLY:
            continue
        folder = packages_path / name.lower() / version
        nuspec = next(folder.glob("*.nuspec"), None)
        meta = {"license": "", "url": "", "copyright": "", "authors": ""}
        if nuspec:
            tree = ET.parse(nuspec)
            md = next(e for e in tree.getroot() if e.tag.endswith("metadata"))
            for e in md:
                tag = e.tag.split("}")[-1]
                if tag == "license" and e.get("type") == "expression":
                    meta["license"] = (e.text or "").strip()
                elif tag == "license" and e.get("type") == "file":
                    meta["license_file"] = (e.text or "").strip()
                elif tag == "licenseUrl" and not meta["license"]:
                    meta["license"] = (e.text or "").strip()
                elif tag in ("projectUrl", "copyright", "authors"):
                    meta[{"projectUrl": "url"}.get(tag, tag)] = (e.text or "").strip()
        texts = license_texts(folder)
        if not texts and meta.get("license_file"):
            f = folder / meta["license_file"]
            if f.is_file():
                texts = [f.read_text(encoding="utf-8", errors="replace").strip()]
        yield name, version, meta, texts


def npm_packages():
    lock = json.loads(LOCK.read_text(encoding="utf-8"))["packages"]
    for path, info in sorted(lock.items()):
        if not path or info.get("dev") or info.get("devOptional"):
            continue
        folder = ROOT / "web" / path
        pkg = json.loads((folder / "package.json").read_text(encoding="utf-8")) if (folder / "package.json").is_file() else {}
        repo = pkg.get("repository")
        url = (repo.get("url") if isinstance(repo, dict) else repo) or pkg.get("homepage") or ""
        author = pkg.get("author")
        author = author.get("name") if isinstance(author, dict) else (author or "")
        lic = pkg.get("license") or info.get("license") or ""
        yield path.split("node_modules/")[-1], info.get("version", ""), {"license": lic, "url": url, "copyright": "", "authors": author}, license_texts(folder)


def section(name, version, meta, texts):
    lines = [f"{name} {version}".strip(), f"License: {meta['license'] or 'see below'}"]
    if meta.get("url"):
        lines.append(f"Source: {meta['url']}")
    if meta.get("copyright"):
        lines.append(meta["copyright"])
    lines.append("")
    if texts:
        lines.extend(t + "\n" for t in texts)
    elif meta["license"].upper() == "MIT":
        holder = meta.get("copyright") or f"Copyright (c) {meta.get('authors') or name} contributors"
        lines.append(f"{holder}\n\n{MIT_TEMPLATE.strip()}\n")
    else:
        lines.append(f"See {meta['license']} at https://spdx.org/licenses/{meta['license']}.html\n")
    return "\n".join(lines)


def main():
    if not ASSETS.is_file() or not (ROOT / "web" / "node_modules").is_dir():
        sys.exit("Run `dotnet restore` and `npm ci` (in web/) first.")

    parts = [
        "Stowaway: third-party notices",
        "=" * 30,
        "",
        "Stowaway is Copyright (c) 2026 Thomas Kraft and released under the MIT License (see LICENSE.txt).",
        "It includes the third-party software listed below, under the licenses shown.",
        "",
        "Linux-container engines: restic and rclone run from the restic/restic image and a runner image built from it",
        "on your computer (not distributed with Stowaway); Alpine Linux packages in that image have their own licenses.",
        "Windows-container engines: the restic and rclone programs below are included in the installer.",
        "",
    ]

    licenses = ROOT / "tools" / "licenses"
    parts += ["", "=" * 78, "Bundled programs (Windows-container engines)", "=" * 78, ""]
    for name, version, url, lic, file in [
        ("restic", "0.19.1", "https://github.com/restic/restic", "BSD-2-Clause", "restic-LICENSE.txt"),
        ("rclone", "1.76.0", "https://github.com/rclone/rclone", "MIT", "rclone-COPYING.txt"),
    ]:
        parts += ["-" * 78, f"{name} {version}", f"License: {lic}", f"Source: {url}", "",
                  (licenses / file).read_text(encoding="utf-8").strip(), ""]

    parts += ["", "-" * 78, ".NET runtime, ASP.NET Core and Windows Desktop (self-contained)", "-" * 78, "",
              "License: MIT. Copyright (c) .NET Foundation and Contributors.",
              "Source and the runtime's own third-party notices:",
              "  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT",
              "  https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt",
              "  https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT",
              "",
              "WiX Toolset custom actions (embedded in the installer)",
              "License: MS-RL. Copyright (c) .NET Foundation and contributors. https://github.com/wixtoolset/wix",
              ""]

    parts += ["", "=" * 78, "NuGet packages (service)", "=" * 78, ""]
    for name, version, meta, texts in nuget_packages():
        parts += ["-" * 78, section(name, version, meta, texts)]

    parts += ["", "=" * 78, "npm packages (web interface)", "=" * 78, ""]
    for name, version, meta, texts in npm_packages():
        parts += ["-" * 78, section(name, version, meta, texts)]

    OUT.write_text("\n".join(parts).rstrip() + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
