#!/usr/bin/env bash
# Windows 上验证 DesktopPetCore（养成系统/活动模型/情绪聚合等纯逻辑层）。
#
# 为什么不用 `swift test`：SwiftPM 在 Windows 上 spawn swiftc 时存在已知
# bug（llbuild job 执行，表现为 "error: fatalError" 无详情），但手动 swiftc
# 完全正常。本脚本用纯手动流程：编译 core -> 编译测试 -> 生成测试入口 ->
# 链接 XCTest -> 运行。在 git-bash（MSYS2）下执行。
#
# 用法: ./scripts/verify-core-windows.sh

set -euo pipefail

# ---- 定位 Swift 工具链（自动探测） ----
find_swift_root() {
  # 1) PATH 里的 swift（swift 在 Toolchains/<ver>/usr/bin 下）
  local sw
  sw=$(command -v swift 2>/dev/null || true)
  if [ -n "$sw" ]; then
    local root
    root=$(cd "$(dirname "$sw")/../../../../.." 2>/dev/null && pwd)
    [ -d "$root/Platforms" ] && { echo "$root"; return; }
  fi
  # 2) 常见安装位置
  for cand in \
    "$LOCALAPPDATA/Programs/Swift" \
    "$HOME/AppData/Local/Programs/Swift" \
    "/c/soft/swift" \
    "/c/Program Files/Swift" \
    "/d/swift" \
    "/d/tools/swift"; do
    [ -d "$cand/Platforms" ] && { echo "$cand"; return; }
  done
  echo ""
}

SWIFT_ROOT=$(find_swift_root)
if [ -z "$SWIFT_ROOT" ]; then
  echo "错误：找不到 Swift 安装目录（探测过 LocalAppData/Programs/Swift、C:\\soft\\swift 等）。"
  echo "安装：https://www.swift.org/install/windows/ 或双击官方安装器。"
  exit 1
fi
echo "Swift 根目录: $SWIFT_ROOT"

PLATFORM_VER=$(ls "$SWIFT_ROOT/Platforms" | grep -E '^[0-9]' | sort -V | tail -1)
SDKROOT="$SWIFT_ROOT/Platforms/$PLATFORM_VER/Windows.platform/Developer/SDKs/Windows.sdk"
TOOLCHAIN=$(ls "$SWIFT_ROOT/Toolchains" | head -1)
export PATH="$SWIFT_ROOT/Runtimes/$PLATFORM_VER/usr/bin:$SWIFT_ROOT/Toolchains/$TOOLCHAIN/usr/bin:$PATH"
export SDKROOT

XCTEST_LIB="$SWIFT_ROOT/Platforms/$PLATFORM_VER/Windows.platform/Developer/Library/XCTest-$PLATFORM_VER/usr/lib/swift/windows"
XCTEST_BIN="$SWIFT_ROOT/Platforms/$PLATFORM_VER/Windows.platform/Developer/Library/XCTest-$PLATFORM_VER/usr/bin64"

REPO=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "== 1/4 编译 DesktopPetCore =="
cd "$WORK"
swiftc -c "$REPO"/Sources/DesktopPetCore/*.swift \
  -module-name DesktopPetCore -parse-as-library -enable-testing \
  -emit-module -emit-module-path "$WORK/DesktopPetCore.swiftmodule" \
  -sdk "$SDKROOT" -I "$WORK" -I "$XCTEST_LIB" -I "$XCTEST_LIB/x86_64" \
  -Xcc -D_MT -Xcc -D_DLL

echo "== 2/4 编译测试 =="
swiftc -c "$REPO"/Tests/DesktopPetCoreTests/*.swift \
  -module-name DesktopPetCoreTests -parse-as-library -enable-testing \
  -emit-module -emit-module-path "$WORK/DesktopPetCoreTests.swiftmodule" \
  -sdk "$SDKROOT" -I "$WORK" -I "$XCTEST_LIB" -I "$XCTEST_LIB/x86_64"

echo "== 3/4 生成测试入口并链接 =="
python3 - "$REPO" "$WORK" <<'PYEOF'
import re, glob, sys
repo, work = sys.argv[1], sys.argv[2]
classes = {}
for f in glob.glob(repo + '/Tests/DesktopPetCoreTests/*.swift'):
    s = open(f, encoding='utf-8').read()
    for m in re.finditer(r'final class (\w+): XCTestCase', s):
        cls = m.group(1)
        classes[cls] = re.findall(r'func (test\w+)\(', s[m.start():])
lines = ["import XCTest", "@testable import DesktopPetCoreTests", ""]
for cls, tests in classes.items():
    lines.append(f"extension {cls} {{")
    lines.append("    static let allTests = [")
    for t in tests:
        lines.append(f'        ("{t}", {t}),')
    lines.append("    ]")
    lines.append("}")
    lines.append("")
lines.append("XCTMain([")
for cls in classes:
    lines.append(f"    testCase({cls}.allTests),")
lines.append("])")
open(work + '/runner.swift', 'w', encoding='utf-8').write('\n'.join(lines))
print(f"    发现 {len(classes)} 个测试类")
PYEOF
cd "$WORK"
swiftc *.o runner.swift -o tests.exe \
  -sdk "$SDKROOT" -I "$WORK" -I "$XCTEST_LIB" -L "$XCTEST_LIB/x86_64" -lXCTest

echo "== 4/4 运行测试 =="
export PATH="$XCTEST_BIN:$PATH"
"$WORK/tests.exe"
