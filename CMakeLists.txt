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
option(KHZ_ENABLE_LEDGER "Compile the SQLite proof-chain ledger" ON)
option(KHZ_BUILD_SELFTEST "Build the self-test executable" ON)
option(KHZ_BUILD_SHARED "Build the shared library used by the C# bindings" ON)

# Sources are listed, not globbed. A glob would silently pick up any stray .c
# file left in the tree and produce duplicate symbols; an explicit list fails
# loudly instead, which is the behaviour worth having.
#
# Two modules are split across multiple translation units, and in both cases
# every half is required or the link fails:
#   khz_formula.c        IR builder and parser
#   khz_formula_eval.c   evaluator: a tree to a value
#   khz_formula_deps.c   dependency declaration, setting, recalculation
#   khz_xlsx_reader.c        zip layer: central directory, DEFLATE, CRC-32
#   khz_xlsx_reader_parse.c  XML layer: shared strings, worksheet to grid
#
# khz_formula_deps.c was split out of khz_formula_eval.c in Phase 96. Omitting
# it does not produce a warning, it produces undefined references to
# khz_formula_set, khz_formula_declare_dependencies and khz_formula_recalc,
# all three of which the selftest calls.
#
# khz_inflate.c arrived in Phase 97 and the zip layer now calls into it, so
# omitting it breaks the link rather than merely disabling a feature.
set(KHZ_SOURCES
    src/khz_arena.c
    src/khz_hash.c
    src/khz_rational.c
    src/khz_cell.c
    src/khz_grid.c
    src/khz_simd.c
    src/khz_sheet.c
    src/khz_formula.c
    src/khz_formula_eval.c
    src/khz_formula_deps.c
    src/khz_inflate.c
    src/khz_xlsx.c
    src/khz_xlsx_reader.c
    src/khz_xlsx_reader_parse.c
    src/khz_ledger.c
    src/khz_abi.c
)

# One object library compiled once, then wrapped as both a static archive and a
# shared object. The C# bindings need a shared library to P/Invoke into; the
# static archive is what a C consumer wants. Compiling the sources twice would
# let the two drift apart under different flags.
add_library(khz_sheet_objects OBJECT ${KHZ_SOURCES})
set_target_properties(khz_sheet_objects PROPERTIES POSITION_INDEPENDENT_CODE ON)

target_include_directories(khz_sheet_objects PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>
)

if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(khz_sheet_objects PRIVATE -Wall -Wextra -Wshadow -Wvla)
    target_compile_options(khz_sheet_objects PRIVATE $<$<CONFIG:Release>:-O3>)
endif()

# -mavx2 is only applied when the compiler actually accepts it. Forcing it
# unconditionally would break every non-x86 build, and khz_simd.c already
# selects NEON or scalar from the predefined macros, so the kernel that gets
# compiled always matches the flags that were used.
include(CheckCCompilerFlag)
check_c_compiler_flag("-mavx2" KHZ_COMPILER_ACCEPTS_MAVX2)

if(KHZ_ENABLE_AVX2 AND KHZ_COMPILER_ACCEPTS_MAVX2)
    target_compile_options(khz_sheet_objects PRIVATE -mavx2)
    message(STATUS "khz_sheet: AVX2 kernel enabled (-mavx2)")
else()
    message(STATUS "khz_sheet: AVX2 kernel not enabled; NEON or scalar kernel will be used")
endif()

# sqlite3 is the one permitted external dependency and must come from the
# system: never vendored, never fetched. When the ledger is requested and
# sqlite3 is absent, configuration fails loudly rather than quietly producing a
# library whose ledger silently refuses every call.
set(KHZ_LEDGER_ACTIVE OFF)

if(KHZ_ENABLE_LEDGER)
    find_path(KHZ_SQLITE3_INCLUDE_DIR sqlite3.h)
    find_library(KHZ_SQLITE3_LIBRARY NAMES sqlite3)

    if(KHZ_SQLITE3_INCLUDE_DIR AND KHZ_SQLITE3_LIBRARY)
        set(KHZ_LEDGER_ACTIVE ON)
        target_include_directories(khz_sheet_objects PRIVATE ${KHZ_SQLITE3_INCLUDE_DIR})
        target_compile_definitions(khz_sheet_objects PRIVATE KHZ_WITH_SQLITE=1)
        message(STATUS "khz_sheet: ledger enabled against ${KHZ_SQLITE3_LIBRARY}")
    else()
        message(FATAL_ERROR
            "KHZ_ENABLE_LEDGER is ON but system sqlite3 was not found. "
            "Install the sqlite3 development package, or configure with "
            "-DKHZ_ENABLE_LEDGER=OFF to build without the ledger.")
    endif()
else()
    message(STATUS "khz_sheet: ledger disabled; khz_ledger_* will return ERR_UNSUPPORTED")
endif()

add_library(khz_sheet STATIC $<TARGET_OBJECTS:khz_sheet_objects>)
target_include_directories(khz_sheet PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>
)

if(KHZ_LEDGER_ACTIVE)
    target_link_libraries(khz_sheet PUBLIC ${KHZ_SQLITE3_LIBRARY})
endif()

if(KHZ_BUILD_SHARED)
    add_library(khz_sheet_shared SHARED $<TARGET_OBJECTS:khz_sheet_objects>)

    # The C# side loads "khz_sheet", so both artefacts carry that base name.
    set_target_properties(khz_sheet_shared PROPERTIES OUTPUT_NAME khz_sheet)

    target_include_directories(khz_sheet_shared PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    )

    if(KHZ_LEDGER_ACTIVE)
        target_link_libraries(khz_sheet_shared PUBLIC ${KHZ_SQLITE3_LIBRARY})
    endif()
endif()

# khz_simd.h is included here deliberately. This generated file calls
# khz_simd_kernel, khz_simd_kernel_name and khz_simd_lanes, and until Phase 95
# it included only khz_abi.h, khz_ledger.h and khz_sheet.h. Those three
# declarations arrived only transitively, through khz_sheet.h, so the file was
# not strictly ill-formed - but it had never been compiled either way, and
# depending on a transitive include for a direct call is not a property worth
# relying on.
#
# khz_inflate_selftest is added into the same failure count rather than merely
# reported. Its vectors are the only known-answer tests in the project that
# were run before the code was committed, and a test that only ever ran on the
# author's machine is not a test the project has. Anything it catches now fails
# ctest.
if(KHZ_BUILD_SELFTEST)
    file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/khz_selftest_main.c"
"#include <stdio.h>\n"
"#include \"khz_abi.h\"\n"
"#include \"khz_inflate.h\"\n"
"#include \"khz_ledger.h\"\n"
"#include \"khz_sheet.h\"\n"
"#include \"khz_simd.h\"\n"
"int main(void)\n"
"{\n"
"    int failures = khz_sheet_selftest();\n"
"    int inflate_failures = khz_inflate_selftest();\n"
"    failures += inflate_failures;\n"
"    printf(\"abi=%u kernel=%s lanes=%zu ledger=%d xlsx=%d reader=%d ranges=%d inflate=%d inflate_failures=%d failures=%d\\n\",\n"
"           khz_abi_version(),\n"
"           khz_simd_kernel_name(khz_simd_kernel()),\n"
"           khz_simd_lanes(),\n"
"           khz_ledger_available(),\n"
"           khz_abi_xlsx_compiled(),\n"
"           khz_abi_xlsx_reader_compiled(),\n"
"           khz_abi_range_edges_compiled(),\n"
"           khz_inflate_available(),\n"
"           inflate_failures,\n"
"           failures);\n"
"    return failures == 0 ? 0 : 1;\n"
"}\n")

    add_executable(khz_selftest "${CMAKE_CURRENT_BINARY_DIR}/khz_selftest_main.c")
    target_link_libraries(khz_selftest PRIVATE khz_sheet)

    enable_testing()
    add_test(NAME khz_selftest COMMAND khz_selftest)
endif()
