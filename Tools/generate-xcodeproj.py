#!/usr/bin/env python3
"""Emit HelmsleyDrive.xcodeproj/project.pbxproj.

Hand-writing a pbxproj is error-prone mostly because every object cross-references every other by
a 24-hex-digit id; generating it means those ids are allocated once and referenced by name.
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


SHARED = ["Configuration.swift", "Log.swift", "TokenStore.swift", "OAuth.swift", "HelmsleyAPI.swift", "ItemIdentity.swift"]
APP = ["HelmsleyDriveApp.swift", "ContentView.swift", "AppModel.swift", "SignIn.swift"]
EXT = ["FileProviderExtension.swift", "FileProviderItem.swift", "FolderEnumerator.swift", "SnapshotStore.swift"]

APP_TARGET = "HelmsleyDrive"
EXT_TARGET = "HelmsleyFileProvider"

lines = []
w = lines.append

w("// !$*UTF8*$!")
w("{")
w("\tarchiveVersion = 1;")
w("\tclasses = {\n\t};")
w("\tobjectVersion = 56;")
w("\tobjects = {")

# ---------------------------------------------------------------- PBXBuildFile
w("\n/* Begin PBXBuildFile section */")


def build_file(target, group, filename, phase="Sources"):
    key = "bf/%s/%s/%s" % (target, group, filename)
    w("\t\t%s /* %s in %s */ = {isa = PBXBuildFile; fileRef = %s /* %s */; };"
      % (oid(key), filename, phase, oid("fr/%s/%s" % (group, filename)), filename))
    return oid(key)


app_sources = [build_file(APP_TARGET, "Shared", f) for f in SHARED]
app_sources += [build_file(APP_TARGET, "HelmsleyDrive", f) for f in APP]
ext_sources = [build_file(EXT_TARGET, "Shared", f) for f in SHARED]
ext_sources += [build_file(EXT_TARGET, "FileProvider", f) for f in EXT]

# Both targets carry the icon: the Dock reads the app's, the Finder sidebar entry for a mounted
# domain reads the extension's. Tools/generate-icon.py builds them from the portal's own mark.
app_resources = [build_file(APP_TARGET, "HelmsleyDrive", "Assets.xcassets", "Resources")]
ext_resources = [build_file(EXT_TARGET, "FileProvider", "Assets.xcassets", "Resources")]

w("\t\t%s /* %s.appex in Embed Foundation Extensions */ = {isa = PBXBuildFile; fileRef = %s /* %s.appex */; "
  "settings = {ATTRIBUTES = (RemoveHeadersOnCopy, ); }; };"
  % (oid("bf/embed"), EXT_TARGET, oid("product/ext"), EXT_TARGET))
w("/* End PBXBuildFile section */")

# ------------------------------------------------------- PBXCopyFilesBuildPhase
w("\n/* Begin PBXCopyFilesBuildPhase section */")
w("\t\t%s /* Embed Foundation Extensions */ = {" % oid("phase/embed"))
w("\t\t\tisa = PBXCopyFilesBuildPhase;")
w("\t\t\tbuildActionMask = 2147483647;")
w('\t\t\tdstPath = "";')
w("\t\t\tdstSubfolderSpec = 13;")
w("\t\t\tfiles = (")
w("\t\t\t\t%s /* %s.appex in Embed Foundation Extensions */," % (oid("bf/embed"), EXT_TARGET))
w("\t\t\t);")
w('\t\t\tname = "Embed Foundation Extensions";')
w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
w("\t\t};")
w("/* End PBXCopyFilesBuildPhase section */")

# ----------------------------------------------------------- PBXFileReference
w("\n/* Begin PBXFileReference section */")


def file_ref(group, filename, path=None, ftype="sourcecode.swift"):
    w('\t\t%s /* %s */ = {isa = PBXFileReference; lastKnownFileType = %s; path = %s; sourceTree = "<group>"; };'
      % (oid("fr/%s/%s" % (group, filename)), filename, ftype, path or filename))


for f in SHARED:
    file_ref("Shared", f)
for f in APP:
    file_ref("HelmsleyDrive", f)
for f in EXT:
    file_ref("FileProvider", f)

file_ref("HelmsleyDrive", "Info.plist", ftype="text.plist.xml")
file_ref("HelmsleyDrive", "HelmsleyDrive.entitlements", ftype="text.plist.entitlements")
file_ref("HelmsleyDrive", "Assets.xcassets", ftype="folder.assetcatalog")
file_ref("FileProvider", "Info.plist", ftype="text.plist.xml")
file_ref("FileProvider", "FileProvider.entitlements", ftype="text.plist.entitlements")
file_ref("FileProvider", "Assets.xcassets", ftype="folder.assetcatalog")

w('\t\t%s /* %s.app */ = {isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; '
  'path = %s.app; sourceTree = BUILT_PRODUCTS_DIR; };' % (oid("product/app"), APP_TARGET, APP_TARGET))
w('\t\t%s /* %s.appex */ = {isa = PBXFileReference; explicitFileType = "wrapper.app-extension"; includeInIndex = 0; '
  'path = %s.appex; sourceTree = BUILT_PRODUCTS_DIR; };' % (oid("product/ext"), EXT_TARGET, EXT_TARGET))
w("/* End PBXFileReference section */")

# ------------------------------------------------------------------- PBXGroup
w("\n/* Begin PBXGroup section */")


def group(key, name, children, path=None):
    w("\t\t%s /* %s */ = {" % (oid(key), name))
    w("\t\t\tisa = PBXGroup;")
    w("\t\t\tchildren = (")
    for child_id, child_name in children:
        w("\t\t\t\t%s /* %s */," % (child_id, child_name))
    w("\t\t\t);")
    # The main group carries neither: it is the project root, and an empty `name = ;` is a syntax
    # error rather than an absent one.
    if path:
        w("\t\t\tpath = %s;" % path)
    elif name:
        w("\t\t\tname = %s;" % name)
    w('\t\t\tsourceTree = "<group>";')
    w("\t\t};")


group("group/shared", "Shared",
      [(oid("fr/Shared/%s" % f), f) for f in SHARED], path="Shared")
group("group/app", "HelmsleyDrive",
      [(oid("fr/HelmsleyDrive/%s" % f), f) for f in APP]
      + [(oid("fr/HelmsleyDrive/Assets.xcassets"), "Assets.xcassets"),
         (oid("fr/HelmsleyDrive/Info.plist"), "Info.plist"),
         (oid("fr/HelmsleyDrive/HelmsleyDrive.entitlements"), "HelmsleyDrive.entitlements")],
      path="HelmsleyDrive")
group("group/ext", "FileProvider",
      [(oid("fr/FileProvider/%s" % f), f) for f in EXT]
      + [(oid("fr/FileProvider/Assets.xcassets"), "Assets.xcassets"),
         (oid("fr/FileProvider/Info.plist"), "Info.plist"),
         (oid("fr/FileProvider/FileProvider.entitlements"), "FileProvider.entitlements")],
      path="FileProvider")
group("group/products", "Products",
      [(oid("product/app"), "%s.app" % APP_TARGET), (oid("product/ext"), "%s.appex" % EXT_TARGET)])
group("group/root", "", [(oid("group/shared"), "Shared"),
                         (oid("group/app"), "HelmsleyDrive"),
                         (oid("group/ext"), "FileProvider"),
                         (oid("group/products"), "Products")])
w("/* End PBXGroup section */")

# ------------------------------------------------------- PBXFrameworks/Resources
w("\n/* Begin PBXFrameworksBuildPhase section */")
for key in ("phase/frameworks/app", "phase/frameworks/ext"):
    w("\t\t%s /* Frameworks */ = {" % oid(key))
    w("\t\t\tisa = PBXFrameworksBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (\n\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
w("/* End PBXFrameworksBuildPhase section */")

w("\n/* Begin PBXResourcesBuildPhase section */")
for key, files in (("phase/resources/app", app_resources), ("phase/resources/ext", ext_resources)):
    w("\t\t%s /* Resources */ = {" % oid(key))
    w("\t\t\tisa = PBXResourcesBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (")
    for f in files:
        w("\t\t\t\t%s," % f)
    w("\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
w("/* End PBXResourcesBuildPhase section */")

# ------------------------------------------------------------ PBXSourcesBuildPhase
w("\n/* Begin PBXSourcesBuildPhase section */")
for key, files in (("phase/sources/app", app_sources), ("phase/sources/ext", ext_sources)):
    w("\t\t%s /* Sources */ = {" % oid(key))
    w("\t\t\tisa = PBXSourcesBuildPhase;")
    w("\t\t\tbuildActionMask = 2147483647;")
    w("\t\t\tfiles = (")
    for f in files:
        w("\t\t\t\t%s," % f)
    w("\t\t\t);")
    w("\t\t\trunOnlyForDeploymentPostprocessing = 0;")
    w("\t\t};")
w("/* End PBXSourcesBuildPhase section */")

# ------------------------------------------------------------ Target dependency
w("\n/* Begin PBXContainerItemProxy section */")
w("\t\t%s /* PBXContainerItemProxy */ = {" % oid("proxy/ext"))
w("\t\t\tisa = PBXContainerItemProxy;")
w("\t\t\tcontainerPortal = %s /* Project object */;" % oid("project"))
w("\t\t\tproxyType = 1;")
w("\t\t\tremoteGlobalIDString = %s;" % oid("target/ext"))
w("\t\t\tremoteInfo = %s;" % EXT_TARGET)
w("\t\t};")
w("/* End PBXContainerItemProxy section */")

w("\n/* Begin PBXTargetDependency section */")
w("\t\t%s /* PBXTargetDependency */ = {" % oid("dep/ext"))
w("\t\t\tisa = PBXTargetDependency;")
w("\t\t\ttarget = %s /* %s */;" % (oid("target/ext"), EXT_TARGET))
w("\t\t\ttargetProxy = %s /* PBXContainerItemProxy */;" % oid("proxy/ext"))
w("\t\t};")
w("/* End PBXTargetDependency section */")

# ------------------------------------------------------------- PBXNativeTarget
w("\n/* Begin PBXNativeTarget section */")

w("\t\t%s /* %s */ = {" % (oid("target/app"), APP_TARGET))
w("\t\t\tisa = PBXNativeTarget;")
w('\t\t\tbuildConfigurationList = %s /* Build configuration list for PBXNativeTarget "%s" */;'
  % (oid("configlist/app"), APP_TARGET))
w("\t\t\tbuildPhases = (")
w("\t\t\t\t%s /* Sources */," % oid("phase/sources/app"))
w("\t\t\t\t%s /* Frameworks */," % oid("phase/frameworks/app"))
w("\t\t\t\t%s /* Resources */," % oid("phase/resources/app"))
w("\t\t\t\t%s /* Embed Foundation Extensions */," % oid("phase/embed"))
w("\t\t\t);")
w("\t\t\tbuildRules = (\n\t\t\t);")
w("\t\t\tdependencies = (")
w("\t\t\t\t%s /* PBXTargetDependency */," % oid("dep/ext"))
w("\t\t\t);")
w("\t\t\tname = %s;" % APP_TARGET)
w("\t\t\tproductName = %s;" % APP_TARGET)
w("\t\t\tproductReference = %s /* %s.app */;" % (oid("product/app"), APP_TARGET))
w('\t\t\tproductType = "com.apple.product-type.application";')
w("\t\t};")

w("\t\t%s /* %s */ = {" % (oid("target/ext"), EXT_TARGET))
w("\t\t\tisa = PBXNativeTarget;")
w('\t\t\tbuildConfigurationList = %s /* Build configuration list for PBXNativeTarget "%s" */;'
  % (oid("configlist/ext"), EXT_TARGET))
w("\t\t\tbuildPhases = (")
w("\t\t\t\t%s /* Sources */," % oid("phase/sources/ext"))
w("\t\t\t\t%s /* Frameworks */," % oid("phase/frameworks/ext"))
w("\t\t\t\t%s /* Resources */," % oid("phase/resources/ext"))
w("\t\t\t);")
w("\t\t\tbuildRules = (\n\t\t\t);")
w("\t\t\tdependencies = (\n\t\t\t);")
w("\t\t\tname = %s;" % EXT_TARGET)
w("\t\t\tproductName = %s;" % EXT_TARGET)
w("\t\t\tproductReference = %s /* %s.appex */;" % (oid("product/ext"), EXT_TARGET))
w('\t\t\tproductType = "com.apple.product-type.app-extension";')
w("\t\t};")
w("/* End PBXNativeTarget section */")

# ------------------------------------------------------------------ PBXProject
w("\n/* Begin PBXProject section */")
w("\t\t%s /* Project object */ = {" % oid("project"))
w("\t\t\tisa = PBXProject;")
w("\t\t\tattributes = {")
w("\t\t\t\tBuildIndependentTargetsInParallel = 1;")
w("\t\t\t\tLastSwiftUpdateCheck = 2660;")
w("\t\t\t\tLastUpgradeCheck = 2660;")
w("\t\t\t\tTargetAttributes = {")
w("\t\t\t\t\t%s = {" % oid("target/app"))
w("\t\t\t\t\t\tCreatedOnToolsVersion = 26.0;")
w("\t\t\t\t\t};")
w("\t\t\t\t\t%s = {" % oid("target/ext"))
w("\t\t\t\t\t\tCreatedOnToolsVersion = 26.0;")
w("\t\t\t\t\t};")
w("\t\t\t\t};")
w("\t\t\t};")
w('\t\t\tbuildConfigurationList = %s /* Build configuration list for PBXProject "HelmsleyDrive" */;'
  % oid("configlist/project"))
w("\t\t\tcompatibilityVersion = \"Xcode 14.0\";")
w("\t\t\tdevelopmentRegion = en;")
w("\t\t\thasScannedForEncodings = 0;")
w("\t\t\tknownRegions = (")
w("\t\t\t\ten,")
w("\t\t\t\tBase,")
w("\t\t\t);")
w("\t\t\tmainGroup = %s;" % oid("group/root"))
w("\t\t\tproductRefGroup = %s /* Products */;" % oid("group/products"))
w('\t\t\tprojectDirPath = "";')
w('\t\t\tprojectRoot = "";')
w("\t\t\ttargets = (")
w("\t\t\t\t%s /* %s */," % (oid("target/app"), APP_TARGET))
w("\t\t\t\t%s /* %s */," % (oid("target/ext"), EXT_TARGET))
w("\t\t\t);")
w("\t\t};")
w("/* End PBXProject section */")

# ------------------------------------------------------------ XCBuildConfiguration
PROJECT_COMMON = {
    "ALWAYS_SEARCH_USER_PATHS": "NO",
    "CLANG_ENABLE_MODULES": "YES",
    "CLANG_ENABLE_OBJC_ARC": "YES",
    "COPY_PHASE_STRIP": "NO",
    "ENABLE_HARDENED_RUNTIME": "YES",
    "ENABLE_STRICT_OBJC_MSGSEND": "YES",
    "GCC_NO_COMMON_BLOCKS": "YES",
    "MACOSX_DEPLOYMENT_TARGET": "14.0",
    "SDKROOT": "macosx",
    "SWIFT_VERSION": "5.0",
    # The project has no shipping-vs-development split beyond optimisation, and automatic signing
    # with the team below is what makes the App Group and keychain entitlements resolvable.
    "CODE_SIGN_STYLE": "Automatic",
    # The team, not the identifier in a signing certificate's common name — those differ, and the
    # wrong one here fails as "No Account for Team ..." at signing time.
    "DEVELOPMENT_TEAM": "CR2F6D8AF7",
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

APP_COMMON = {
    "ASSETCATALOG_COMPILER_APPICON_NAME": "AppIcon",
    "CODE_SIGN_ENTITLEMENTS": "HelmsleyDrive/HelmsleyDrive.entitlements",
    "COMBINE_HIDPI_IMAGES": "YES",
    "CURRENT_PROJECT_VERSION": "1",
    "ENABLE_PREVIEWS": "YES",
    "GENERATE_INFOPLIST_FILE": "NO",
    "INFOPLIST_FILE": "HelmsleyDrive/Info.plist",
    "LD_RUNPATH_SEARCH_PATHS": '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/../Frameworks",\n\t\t\t\t)',
    "MARKETING_VERSION": "1.0",
    "PRODUCT_BUNDLE_IDENTIFIER": "uk.co.helmsley.HelmsleyDrive",
    "PRODUCT_NAME": '"$(TARGET_NAME)"',
    "SWIFT_EMIT_LOC_STRINGS": "YES",
}
EXT_COMMON = {
    "ASSETCATALOG_COMPILER_APPICON_NAME": "AppIcon",
    "CODE_SIGN_ENTITLEMENTS": "FileProvider/FileProvider.entitlements",
    "CURRENT_PROJECT_VERSION": "1",
    "GENERATE_INFOPLIST_FILE": "NO",
    "INFOPLIST_FILE": "FileProvider/Info.plist",
    "LD_RUNPATH_SEARCH_PATHS": '(\n\t\t\t\t\t"$(inherited)",\n\t\t\t\t\t"@executable_path/../../../../Frameworks",\n\t\t\t\t)',
    "MARKETING_VERSION": "1.0",
    "PRODUCT_BUNDLE_IDENTIFIER": "uk.co.helmsley.HelmsleyDrive.FileProvider",
    # Not "FileProvider": the module name is derived from this, and a module with the same name as
    # the system framework it imports cannot see that framework at all.
    "PRODUCT_NAME": '"$(TARGET_NAME)"',
    "SKIP_INSTALL": "YES",
    "SWIFT_EMIT_LOC_STRINGS": "YES",
}


def build_config(key, name, settings):
    w("\t\t%s /* %s */ = {" % (oid(key), name))
    w("\t\t\tisa = XCBuildConfiguration;")
    w("\t\t\tbuildSettings = {")
    for setting in sorted(settings):
        w("\t\t\t\t%s = %s;" % (setting, settings[setting]))
    w("\t\t\t};")
    w("\t\t\tname = %s;" % name)
    w("\t\t};")


w("\n/* Begin XCBuildConfiguration section */")
build_config("config/project/Debug", "Debug", dict(PROJECT_COMMON, **PROJECT_DEBUG))
build_config("config/project/Release", "Release", dict(PROJECT_COMMON, **PROJECT_RELEASE))
build_config("config/app/Debug", "Debug", APP_COMMON)
build_config("config/app/Release", "Release", APP_COMMON)
build_config("config/ext/Debug", "Debug", EXT_COMMON)
build_config("config/ext/Release", "Release", EXT_COMMON)
w("/* End XCBuildConfiguration section */")

w("\n/* Begin XCConfigurationList section */")
for key, label, prefix in (
    ("configlist/project", 'Build configuration list for PBXProject "HelmsleyDrive"', "config/project"),
    ("configlist/app", 'Build configuration list for PBXNativeTarget "%s"' % APP_TARGET, "config/app"),
    ("configlist/ext", 'Build configuration list for PBXNativeTarget "%s"' % EXT_TARGET, "config/ext"),
):
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

# A shared scheme, so `xcodebuild -scheme HelmsleyDrive` works without opening Xcode first.
scheme_dir = os.path.join(PROJ, "xcshareddata", "xcschemes")
os.makedirs(scheme_dir, exist_ok=True)
scheme = """<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "2660" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "{app}"
               BuildableName = "{APP}.app"
               BlueprintName = "{APP}"
               ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
            </BuildableReference>
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{app}"
            BuildableName = "{APP}.app"
            BlueprintName = "{APP}"
            ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
      <BuildableProductRunnable runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "{app}"
            BuildableName = "{APP}.app"
            BlueprintName = "{APP}"
            ReferencedContainer = "container:HelmsleyDrive.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction buildConfiguration = "Debug"></AnalyzeAction>
   <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES"></ArchiveAction>
</Scheme>
""".replace("{app}", oid("target/app")).replace("{APP}", APP_TARGET)
with open(os.path.join(scheme_dir, "%s.xcscheme" % APP_TARGET), "w") as fh:
    fh.write(scheme)

print("wrote", os.path.join(PROJ, "project.pbxproj"))
