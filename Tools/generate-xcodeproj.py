#!/usr/bin/env python3
"""Emit HelmsleyDrive.xcodeproj/project.pbxproj.

Hand-writing a pbxproj is error-prone mostly because every object cross-references every other by a
24-hex-digit id; generating it means those ids are allocated once and referenced by name.

Four targets, two platforms. The engine — Shared/ and FileProvider/ — is compiled into all of them
rather than shared through a framework: a file provider extension and its host app already have to
embed their code, and a framework would add an embedding step and a dylib to sign for no gain at
this size.
"""
import os

# Derived from where this script sits, not written out: the checkout has been renamed once already,
# and a hard-coded path is a generator that silently writes to the old copy.
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "HelmsleyDrive.xcodeproj")

_counter = [0]
_ids = {}


def oid(name):
    if name not in _ids:
        _counter[0] += 1
        _ids[name] = "HD%022X" % _counter[0]
    return _ids[name]


# --- what goes where -----------------------------------------------------------------------------

# The engine, compiled into all four targets.
SHARED = ["Configuration.swift", "Log.swift", "TokenStore.swift", "OAuth.swift", "HelmsleyAPI.swift",
          "ItemIdentity.swift"]
# The container apps' shared logic, compiled into the two app targets only. Not the extensions':
# signing in is interactive, and `UIApplication.shared` — which presents the sheet — is barred
# outright in an iOS app extension.
APP_SHARED = ["AppModel.swift", "SignIn.swift"]
ENGINE = ["FileProviderExtension.swift", "FileProviderItem.swift", "FolderEnumerator.swift", "SnapshotStore.swift"]
MAC_UI = ["HelmsleyDriveApp.swift", "ContentView.swift"]
IOS_UI = ["HelmsleyDriveApp.swift", "ContentView.swift"]

APP_BUNDLE_ID = "uk.co.helmsley.HelmsleyDrive"
EXT_BUNDLE_ID = "uk.co.helmsley.HelmsleyDrive.FileProvider"

# One App ID per platform pair, so both platforms are one app in App Store Connect and one
# TestFlight build stream.
TARGETS = [
    dict(key="mac-app", name="HelmsleyDrive", group="HelmsleyDrive", platform="macos", kind="app",
         sources=SHARED + APP_SHARED + MAC_UI, embeds="mac-ext",
         info="HelmsleyDrive/Info.plist", entitlements="HelmsleyDrive/HelmsleyDrive.entitlements",
         bundle_id=APP_BUNDLE_ID),
    dict(key="mac-ext", name="HelmsleyFileProvider", group="FileProvider", platform="macos", kind="ext",
         sources=SHARED + ENGINE,
         info="FileProvider/Info.plist", entitlements="FileProvider/FileProvider.entitlements",
         bundle_id=EXT_BUNDLE_ID),
    dict(key="ios-app", name="HelmsleyDrive-iOS", group="HelmsleyDrive-iOS", platform="ios", kind="app",
         sources=SHARED + APP_SHARED + IOS_UI, embeds="ios-ext",
         info="HelmsleyDrive-iOS/Info.plist", entitlements="HelmsleyDrive-iOS/HelmsleyDrive-iOS.entitlements",
         bundle_id=APP_BUNDLE_ID),
    dict(key="ios-ext", name="HelmsleyFileProvider-iOS", group="FileProvider-iOS", platform="ios", kind="ext",
         sources=SHARED + ENGINE,
         info="FileProvider-iOS/Info.plist", entitlements="FileProvider-iOS/FileProvider-iOS.entitlements",
         bundle_id=EXT_BUNDLE_ID),
]
BY_KEY = {t["key"]: t for t in TARGETS}

# Which group each source file's reference lives in — one reference per file, however many targets
# compile it.
GROUP_OF = {}
for f in SHARED:
    GROUP_OF[f] = "Shared"
for f in APP_SHARED:
    GROUP_OF[f] = "AppShared"
for f in ENGINE:
    GROUP_OF[f] = "FileProvider"

GROUPS = ["Shared", "AppShared", "HelmsleyDrive", "FileProvider", "HelmsleyDrive-iOS", "FileProvider-iOS"]
GROUP_FILES = {
    "Shared": list(SHARED),
    "AppShared": list(APP_SHARED),
    "HelmsleyDrive": MAC_UI + ["Assets.xcassets", "Info.plist", "HelmsleyDrive.entitlements"],
    "FileProvider": ENGINE + ["Assets.xcassets", "Info.plist", "FileProvider.entitlements"],
    "HelmsleyDrive-iOS": IOS_UI + ["Assets.xcassets", "Info.plist", "HelmsleyDrive-iOS.entitlements"],
    "FileProvider-iOS": ["Assets.xcassets", "Info.plist", "FileProvider-iOS.entitlements"],
}

PRODUCT_EXT = {"app": ".app", "ext": ".appex"}
PRODUCT_TYPE = {"app": "com.apple.product-type.application", "ext": "com.apple.product-type.app-extension"}
FILE_TYPE = {"app": "wrapper.application", "ext": '"wrapper.app-extension"'}


def source_group(target, filename):
    """A UI file lives in its own target's group; engine and shared files live in one shared group."""
    return GROUP_OF.get(filename, target["group"])


lines = []
w = lines.append

w("// !$*UTF8*$!")
w("{")
w("\tarchiveVersion = 1;")
w("\tclasses = {\n\t};")
w("\tobjectVersion = 56;")
w("\tobjects = {")

# --- PBXBuildFile --------------------------------------------------------------------------------
w("\n/* Begin PBXBuildFile section */")


def build_file(target_key, group, filename, phase="Sources"):
    key = "bf/%s/%s/%s" % (target_key, group, filename)
    w("\t\t%s /* %s in %s */ = {isa = PBXBuildFile; fileRef = %s /* %s */; };"
      % (oid(key), filename, phase, oid("fr/%s/%s" % (group, filename)), filename))
    return oid(key)


for t in TARGETS:
    t["source_files"] = [build_file(t["key"], source_group(t, f), f) for f in t["sources"]]
    # Every target carries the icon: the Dock and Home Screen read the app's, and the Finder
    # sidebar / Files "Locations" entry for a mounted domain reads the extension's.
    t["resource_files"] = [build_file(t["key"], t["group"], "Assets.xcassets", "Resources")]

for t in TARGETS:
    if t.get("embeds"):
        child = BY_KEY[t["embeds"]]
        w("\t\t%s /* %s.appex in Embed Foundation Extensions */ = {isa = PBXBuildFile; fileRef = %s /* %s.appex */; "
          "settings = {ATTRIBUTES = (RemoveHeadersOnCopy, ); }; };"
          % (oid("bf/embed/%s" % t["key"]), child["name"], oid("product/%s" % child["key"]), child["name"]))
w("/* End PBXBuildFile section */")

# --- PBXCopyFilesBuildPhase ----------------------------------------------------------------------
w("\n/* Begin PBXCopyFilesBuildPhase section */")
for t in TARGETS:
    if not t.get("embeds"):
        continue
    child = BY_KEY[t["embeds"]]
    w("\t\t%s /* Embed Foundation Extensions */ = {" % oid("phase/embed/%s" % t["key"]))
    w("\t\t\tisa = PBXCopyFilesBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w('\t\t\tdstPath = "";')
    w("\t\t\tdstSubfolderSpec = 13;")
    w("\t\t\tfiles = (")
    w("\t\t\t\t%s /* %s.appex in Embed Foundation Extensions */," % (oid("bf/embed/%s" % t["key"]), child["name"]))
    w("\t\t\t);")
    w('\t\t\tname = "Embed Foundation Extensions";')
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
w("/* End PBXCopyFilesBuildPhase section */")

# --- PBXFileReference ----------------------------------------------------------------------------
w("\n/* Begin PBXFileReference section */")

FILE_KINDS = {
    ".swift": "sourcecode.swift",
    ".plist": "text.plist.xml",
    ".entitlements": "text.plist.entitlements",
    ".xcassets": "folder.assetcatalog",
}


def file_ref(group, filename):
    ftype = FILE_KINDS[os.path.splitext(filename)[1]]
    w('\t\t%s /* %s */ = {isa = PBXFileReference; lastKnownFileType = %s; path = %s; sourceTree = "<group>"; };'
      % (oid("fr/%s/%s" % (group, filename)), filename, ftype, filename))


for group in GROUPS:
    for filename in GROUP_FILES[group]:
        file_ref(group, filename)

for t in TARGETS:
    w('\t\t%s /* %s%s */ = {isa = PBXFileReference; explicitFileType = %s; includeInIndex = 0; '
      'path = "%s%s"; sourceTree = BUILT_PRODUCTS_DIR; };'
      % (oid("product/%s" % t["key"]), t["name"], PRODUCT_EXT[t["kind"]], FILE_TYPE[t["kind"]],
         t["name"], PRODUCT_EXT[t["kind"]]))
w("/* End PBXFileReference section */")

# --- PBXGroup ------------------------------------------------------------------------------------
w("\n/* Begin PBXGroup section */")


def group_object(key, name, children, path=None):
    w("\t\t%s /* %s */ = {" % (oid(key), name))
    w("\t\t\tisa = PBXGroup;")
    w("\t\t\tchildren = (")
    for child_id, child_name in children:
        w("\t\t\t\t%s /* %s */," % (child_id, child_name))
    w("\t\t\t);")
    # The main group carries neither: it is the project root, and an empty `name = ;` is a syntax
    # error rather than an absent one.
    if path:
        w('\t\t\tpath = "%s";' % path)
    elif name:
        w("\t\t\tname = %s;" % name)
    w('\t\t\tsourceTree = "<group>";')
    w("\t\t};")


for group in GROUPS:
    group_object("group/%s" % group, group,
                 [(oid("fr/%s/%s" % (group, f)), f) for f in GROUP_FILES[group]], path=group)
group_object("group/products", "Products",
             [(oid("product/%s" % t["key"]), t["name"] + PRODUCT_EXT[t["kind"]]) for t in TARGETS])
group_object("group/root", "",
             [(oid("group/%s" % g), g) for g in GROUPS] + [(oid("group/products"), "Products")])
w("/* End PBXGroup section */")

# --- build phases --------------------------------------------------------------------------------
w("\n/* Begin PBXFrameworksBuildPhase section */")
for t in TARGETS:
    w("\t\t%s /* Frameworks */ = {" % oid("phase/frameworks/%s" % t["key"]))
    w("\t\t\tisa = PBXFrameworksBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (\n\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
w("/* End PBXFrameworksBuildPhase section */")

for section, phase_key, isa, files_key in (
    ("PBXResourcesBuildPhase", "phase/resources", "PBXResourcesBuildPhase", "resource_files"),
    ("PBXSourcesBuildPhase", "phase/sources", "PBXSourcesBuildPhase", "source_files"),
):
    w("\n/* Begin %s section */" % section)
    label = "Resources" if files_key == "resource_files" else "Sources"
    for t in TARGETS:
        w("\t\t%s /* %s */ = {" % (oid("%s/%s" % (phase_key, t["key"])), label))
        w("\t\t\tisa = %s;" % isa)
        w("\t\t\tbuildActionMask = 2147483647;")
        w("\t\t\tfiles = (")
        for f in t[files_key]:
            w("\t\t\t\t%s," % f)
        w("\t\t\t);")
        w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
        w("\t\t};")
    w("/* End %s section */" % section)

# --- dependencies --------------------------------------------------------------------------------
w("\n/* Begin PBXContainerItemProxy section */")
for t in TARGETS:
    if not t.get("embeds"):
        continue
    child = BY_KEY[t["embeds"]]
    w("\t\t%s /* PBXContainerItemProxy */ = {" % oid("proxy/%s" % t["key"]))
    w("\t\t\tisa = PBXContainerItemProxy;")
    w("\t\t\tcontainerPortal = %s /* Project object */;" % oid("project"))
    w("\t\t\tproxyType = 1;")
    w("\t\t\tremoteGlobalIDString = %s;" % oid("target/%s" % child["key"]))
    w('\t\t\tremoteInfo = "%s";' % child["name"])
    w("\t\t};")
w("/* End PBXContainerItemProxy section */")

w("\n/* Begin PBXTargetDependency section */")
for t in TARGETS:
    if not t.get("embeds"):
        continue
    child = BY_KEY[t["embeds"]]
    w("\t\t%s /* PBXTargetDependency */ = {" % oid("dep/%s" % t["key"]))
    w("\t\t\tisa = PBXTargetDependency;")
    w('\t\t\ttarget = %s /* %s */;' % (oid("target/%s" % child["key"]), child["name"]))
    w("\t\t\ttargetProxy = %s /* PBXContainerItemProxy */;" % oid("proxy/%s" % t["key"]))
    w("\t\t};")
w("/* End PBXTargetDependency section */")

# --- targets -------------------------------------------------------------------------------------
w("\n/* Begin PBXNativeTarget section */")
for t in TARGETS:
    w('\t\t%s /* %s */ = {' % (oid("target/%s" % t["key"]), t["name"]))
    w("\t\t\tisa = PBXNativeTarget;")
    w('\t\t\tbuildConfigurationList = %s /* Build configuration list for PBXNativeTarget "%s" */;'
      % (oid("configlist/%s" % t["key"]), t["name"]))
    w("\t\t\tbuildPhases = (")
    w("\t\t\t\t%s /* Sources */," % oid("phase/sources/%s" % t["key"]))
    w("\t\t\t\t%s /* Frameworks */," % oid("phase/frameworks/%s" % t["key"]))
    w("\t\t\t\t%s /* Resources */," % oid("phase/resources/%s" % t["key"]))
    if t.get("embeds"):
        w("\t\t\t\t%s /* Embed Foundation Extensions */," % oid("phase/embed/%s" % t["key"]))
    w("\t\t\t);")
    w("\t\t\tbuildRules = (\n\t\t\t);")
    w("\t\t\tdependencies = (")
    if t.get("embeds"):
        w("\t\t\t\t%s /* PBXTargetDependency */," % oid("dep/%s" % t["key"]))
    w("\t\t\t);")
    w('\t\t\tname = "%s";' % t["name"])
    w('\t\t\tproductName = "%s";' % t["name"])
    w('\t\t\tproductReference = %s /* %s%s */;' % (oid("product/%s" % t["key"]), t["name"], PRODUCT_EXT[t["kind"]]))
    w('\t\t\tproductType = "%s";' % PRODUCT_TYPE[t["kind"]])
    w("\t\t};")
w("/* End PBXNativeTarget section */")

# --- project -------------------------------------------------------------------------------------
w("\n/* Begin PBXProject section */")
w("\t\t%s /* Project object */ = {" % oid("project"))
w("\t\t\tisa = PBXProject;")
w("\t\t\tattributes = {")
w("\t\t\t\tBuildIndependentTargetsInParallel = 1;")
w("\t\t\t\tLastSwiftUpdateCheck = 2700;")
w("\t\t\t\tLastUpgradeCheck = 2700;")
w("\t\t\t\tTargetAttributes = {")
for t in TARGETS:
    w("\t\t\t\t\t%s = {" % oid("target/%s" % t["key"]))
    w("\t\t\t\t\t\tCreatedOnToolsVersion = 27.0;")
    w("\t\t\t\t\t};")
w("\t\t\t\t};")
w("\t\t\t};")
w('\t\t\tbuildConfigurationList = %s /* Build configuration list for PBXProject "HelmsleyDrive" */;'
  % oid("configlist/project"))
w('\t\t\tcompatibilityVersion = "Xcode 14.0";')
w("\t\t\tdevelopmentRegion = en;")
w("\t\t\thasScannedForEncodings = 0;")
w("\t\t\tknownRegions = (\n\t\t\t\ten,\n\t\t\t\tBase,\n\t\t\t);")
w("\t\t\tmainGroup = %s;" % oid("group/root"))
w("\t\t\tproductRefGroup = %s /* Products */;" % oid("group/products"))
w('\t\t\tprojectDirPath = "";')
w('\t\t\tprojectRoot = "";')
w("\t\t\ttargets = (")
for t in TARGETS:
    w('\t\t\t\t%s /* %s */,' % (oid("target/%s" % t["key"]), t["name"]))
w("\t\t\t);")
w("\t\t};")
w("/* End PBXProject section */")

# --- build settings ------------------------------------------------------------------------------
PROJECT_COMMON = {
    "ALWAYS_SEARCH_USER_PATHS": "NO",
    "CLANG_ENABLE_MODULES": "YES",
    "CLANG_ENABLE_OBJC_ARC": "YES",
    "COPY_PHASE_STRIP": "NO",
    "ENABLE_STRICT_OBJC_MSGSEND": "YES",
    "GCC_NO_COMMON_BLOCKS": "YES",
    "SWIFT_VERSION": "5.0",
    "CODE_SIGN_STYLE": "Automatic",
    # The team, not the identifier in a signing certificate's common name — those differ, and the
    # wrong one here fails as "No Account for Team ..." at signing time.
    "DEVELOPMENT_TEAM": "CR2F6D8AF7",
    # One place to bump for a TestFlight build. Info.plist reads both through $(...).
    "MARKETING_VERSION": "1.0",
    "CURRENT_PROJECT_VERSION": "1",
}
PROJECT_DEBUG = {
    "DEBUG_INFORMATION_FORMAT": "dwarf",
    "ENABLE_TESTABILITY": "YES",
    "GCC_OPTIMIZATION_LEVEL": "0",
    "MTL_ENABLE_DEBUG_INFO": "INCLUDE_SOURCE",
    "ONLY_ACTIVE_ARCH": "YES",
    "SWIFT_ACTIVE_COMPILATION_CONDITIONS": "DEBUG",
    "SWIFT_OPTIMIZATION_LEVEL": '"-Onone"',
}
PROJECT_RELEASE = {
    "DEBUG_INFORMATION_FORMAT": '"dwarf-with-dsym"',
    "ENABLE_NS_ASSERTIONS": "NO",
    "MTL_ENABLE_DEBUG_INFO": "NO",
    "SWIFT_COMPILATION_MODE": "wholemodule",
}

PLATFORM = {
    "macos": {
        "SDKROOT": "macosx",
        "MACOSX_DEPLOYMENT_TARGET": "14.0",
        # Required for notarisation, and harmless before it.
        "ENABLE_HARDENED_RUNTIME": "YES",
    },
    "ios": {
        "SDKROOT": "iphoneos",
        # NSFileProviderReplicatedExtension is iOS 16; 17 is the floor worth supporting anyway.
        "IPHONEOS_DEPLOYMENT_TARGET": "17.0",
        "TARGETED_DEVICE_FAMILY": '"1,2"',
        "SUPPORTS_MACCATALYST": "NO",
        # An app extension is not a place to run a UI test host, and this quiets the archive.
        "ENABLE_USER_SCRIPT_SANDBOXING": "YES",
    },
}

RUNPATH = {
    ("macos", "app"): '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/../Frameworks",\n\t\t\t\t)',
    ("macos", "ext"): '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/../../../../Frameworks",\n\t\t\t\t)',
    ("ios", "app"): '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/Frameworks",\n\t\t\t\t)',
    ("ios", "ext"): '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/Frameworks",\n\t\t\t\t\t"@executable_path/../../Frameworks",\n\t\t\t\t)',
}


def target_settings(t):
    settings = dict(PLATFORM[t["platform"]])
    settings.update({
        "ASSETCATALOG_COMPILER_APPICON_NAME": "AppIcon",
        "CODE_SIGN_ENTITLEMENTS": '"%s"' % t["entitlements"],
        "GENERATE_INFOPLIST_FILE": "NO",
        "INFOPLIST_FILE": '"%s"' % t["info"],
        "LD_RUNPATH_SEARCH_PATHS": RUNPATH[(t["platform"], t["kind"])],
        "PRODUCT_BUNDLE_IDENTIFIER": t["bundle_id"],
        "PRODUCT_NAME": '"$(TARGET_NAME)"',
        "SWIFT_EMIT_LOC_STRINGS": "YES",
    })
    if t["kind"] == "ext":
        settings["SKIP_INSTALL"] = "YES"
    if t["platform"] == "macos" and t["kind"] == "app":
        settings["COMBINE_HIDPI_IMAGES"] = "YES"
        settings["ENABLE_PREVIEWS"] = "YES"
    return settings


w("\n/* Begin XCBuildConfiguration section */")


def build_config(key, name, settings):
    w("\t\t%s /* %s */ = {" % (oid(key), name))
    w("\t\t\tisa = XCBuildConfiguration;")
    w("\t\t\tbuildSettings = {")
    for setting in sorted(settings):
        w("\t\t\t\t%s = %s;" % (setting, settings[setting]))
    w("\t\t\t};")
    w("\t\t\tname = %s;" % name)
    w("\t\t};")


build_config("config/project/Debug", "Debug", dict(PROJECT_COMMON, **PROJECT_DEBUG))
build_config("config/project/Release", "Release", dict(PROJECT_COMMON, **PROJECT_RELEASE))
for t in TARGETS:
    for configuration in ("Debug", "Release"):
        build_config("config/%s/%s" % (t["key"], configuration), configuration, target_settings(t))
w("/* End XCBuildConfiguration section */")

w("\n/* Begin XCConfigurationList section */")
lists = [("configlist/project", 'Build configuration list for PBXProject "HelmsleyDrive"', "config/project")]
lists += [("configlist/%s" % t["key"],
           'Build configuration list for PBXNativeTarget "%s"' % t["name"],
           "config/%s" % t["key"]) for t in TARGETS]
for key, label, prefix in lists:
    w("\t\t%s /* %s */ = {" % (oid(key), label))
    w("\t\t\tisa = XCConfigurationList;")
    w("\t\t\tbuildConfigurations = (")
    w("\t\t\t\t%s /* Debug */," % oid("%s/Debug" % prefix))
    w("\t\t\t\t%s /* Release */," % oid("%s/Release" % prefix))
    w("\t\t\t);")
    w("\t\t\tdefaultConfigurationIsVisible = 0;")
    w("\t\t\tdefaultConfigurationName = Release;")
    w("\t\t};")
w("/* End XCConfigurationList section */")

w("\t};")
w("\trootObject = %s /* Project object */;" % oid("project"))
w("}")

os.makedirs(PROJ, exist_ok=True)
with open(os.path.join(PROJ, "project.pbxproj"), "w") as fh:
    fh.write("\n".join(lines) + "\n")

# Shared schemes, so `xcodebuild -scheme ...` works without opening Xcode first.
scheme_dir = os.path.join(PROJ, "xcshareddata", "xcschemes")
os.makedirs(scheme_dir, exist_ok=True)

SCHEME = """<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "2700" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "{id}"
               BuildableName = "{name}.app"
               BlueprintName = "{name}"
               ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
            </BuildableReference>
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{id}"
            BuildableName = "{name}.app"
            BlueprintName = "{name}"
            ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{id}"
            BuildableName = "{name}.app"
            BlueprintName = "{name}"
            ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction buildConfiguration = "Debug"></AnalyzeAction>
   <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES"></ArchiveAction>
</Scheme>
"""

for t in TARGETS:
    if t["kind"] != "app":
        continue
    scheme = SCHEME.replace("{id}", oid("target/%s" % t["key"])).replace("{name}", t["name"])
    with open(os.path.join(scheme_dir, "%s.xcscheme" % t["name"]), "w") as fh:
        fh.write(scheme)

print("wrote %s (%d targets)" % (os.path.join(PROJ, "project.pbxproj"), len(TARGETS)))
