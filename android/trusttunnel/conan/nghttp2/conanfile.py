# SPDX-License-Identifier: Apache-2.0
# Based on the AdGuard 8.1.52 adapter recipe; upstream nghttp2 is MIT licensed.
from conan import ConanFile
from conan.tools.cmake import CMake, CMakeToolchain, cmake_layout
from conan.tools.files import get, copy, patch
from os.path import join


class NGHttp2Conan(ConanFile):
    name = "nghttp2"
    version = "1.68.1"
    license = "MIT"
    settings = "os", "compiler", "build_type", "arch"
    options = {"shared": [True, False], "fPIC": [True, False]}
    default_options = {"shared": False, "fPIC": True}
    exports_sources = "patches/0006_extern_some_internal_func_to_api.patch"

    def config_options(self):
        if self.settings.os == "Windows":
            del self.options.fPIC

    def source(self):
        get(self, "https://github.com/nghttp2/nghttp2/releases/download/v1.68.1/nghttp2-1.68.1.tar.gz",
            sha256="ceb434c1f9dfe2a9d305b6b797786fb9227484dfa88508d14ca1c50263db55d3", strip_root=True)
        # Only export the two declarations needed by TrustTunnel's static library.
        # Do not bring forward patches which relax HTTP validation.
        patch(self, patch_file="patches/0006_extern_some_internal_func_to_api.patch")

    def layout(self):
        cmake_layout(self)

    def generate(self):
        tc = CMakeToolchain(self)
        tc.cache_variables["ENABLE_LIB_ONLY"] = True
        tc.cache_variables["BUILD_STATIC_LIBS"] = True
        tc.cache_variables["BUILD_SHARED_LIBS"] = False
        tc.generate()

    def build(self):
        cmake = CMake(self)
        cmake.configure()
        cmake.build()

    def package(self):
        CMake(self).install()
        copy(self, "COPYING", src=self.source_folder, dst=join(self.package_folder, "licenses"))

    def package_info(self):
        self.cpp_info.libs = ["nghttp2"]
        self.cpp_info.defines = ["NGHTTP2_STATICLIB"]
