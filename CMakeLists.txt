cmake_minimum_required(VERSION 3.16)

project(khz_sheet C)

# C11 exactly, no compiler extensions. The POSIX declarations khz_arena.c needs
# are requested by that file with a feature-test macro rather than by relaxing
# the language mode globally.
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

option(KHZ_ENABLE_AVX2 "Compile the AVX2 SIMD kernel" ON)
option(KHZ_BUILD_SELFTEST "Build the self-test executable" ON)

# Sources are listed, not globbed. A glob would silently pick up any stray .c
# file left in the tree and produce duplicate symbols; an explicit list fails
# loudly instead, which is the behaviour worth having.
add_library(khz_sheet STATIC
    src/khz_arena.c
    src/khz_hash.c
    src/khz_rational.c
    src/khz_cell.c
    src/khz_grid.c
    src/khz_simd.c
    src/khz_sheet.c
)

target_include_directories(khz_sheet PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>
)

if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(khz_sheet PRIVATE -Wall -Wextra -Wshadow -Wvla)
    target_compile_options(khz_sheet PRIVATE $<$<CONFIG:Release>:-O3>)
endif()

# -mavx2 is only applied when the compiler actually accepts it. Forcing it
# unconditionally would break every non-x86 build, and khz_simd.c already
# selects NEON or scalar from the predefined macros, so the kernel that gets
# compiled always matches the flags that were used.
include(CheckCCompilerFlag)
check_c_compiler_flag("-mavx2" KHZ_COMPILER_ACCEPTS_MAVX2)

if(KHZ_ENABLE_AVX2 AND KHZ_COMPILER_ACCEPTS_MAVX2)
    target_compile_options(khz_sheet PRIVATE -mavx2)
    message(STATUS "khz_sheet: AVX2 kernel enabled (-mavx2)")
else()
    message(STATUS "khz_sheet: AVX2 kernel not enabled; NEON or scalar kernel will be used")
endif()

# sqlite3 is the one permitted external dependency and is expected to come from
# the system, never vendored and never fetched. Nothing links it yet: the
# ledger adapter is Phase 91, so declaring the link now would be a dependency
# on code that does not exist.
find_path(KHZ_SQLITE3_INCLUDE_DIR sqlite3.h)
find_library(KHZ_SQLITE3_LIBRARY NAMES sqlite3)

if(KHZ_SQLITE3_INCLUDE_DIR AND KHZ_SQLITE3_LIBRARY)
    message(STATUS "khz_sheet: system sqlite3 found, will be linked by the Phase 91 ledger")
else()
    message(STATUS "khz_sheet: system sqlite3 not found; required only by the Phase 91 ledger")
endif()

if(KHZ_BUILD_SELFTEST)
    file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/khz_selftest_main.c"
"#include <stdio.h>\n"
"#include \"khz_sheet.h\"\n"
"int main(void)\n"
"{\n"
"    int failures = khz_sheet_selftest();\n"
"    printf(\"kernel=%s lanes=%zu failures=%d\\n\",\n"
"           khz_simd_kernel_name(khz_simd_kernel()), khz_simd_lanes(), failures);\n"
"    return failures == 0 ? 0 : 1;\n"
"}\n")

    add_executable(khz_selftest "${CMAKE_CURRENT_BINARY_DIR}/khz_selftest_main.c")
    target_link_libraries(khz_selftest PRIVATE khz_sheet)

    enable_testing()
    add_test(NAME khz_selftest COMMAND khz_selftest)
endif()
