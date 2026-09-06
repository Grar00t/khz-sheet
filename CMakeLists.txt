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
option(KHZ_BUILD_TESTS "Build the end-to-end tests under tests/" ON)
option(KHZ_BUILD_SHARED "Build the shared library used by the C# bindings" ON)
option(KHZ_ENABLE_ASAN "Build with AddressSanitizer" OFF)
option(KHZ_ENABLE_UBSAN "Build with UndefinedBehaviorSanitizer" OFF)

# Enabled unconditionally and at the top level. This used to live inside the
# selftest block, which meant configuring with -DKHZ_BUILD_SELFTEST=OFF
# produced a build with no test infrastructure at all rather than one test
# fewer, and `ctest` in that build reported success over an empty list.
enable_testing()

include(CheckCCompilerFlag)

# Sanitizers.
#
# Applied at directory scope, before any target is defined, so they reach every
# object and every executable including the generated selftest and the tests
# under tests/. A sanitizer that covers the library but not the test harness
# that drives it is close to useless.
#
# Two decisions here are deliberate and worth stating.
#
# First: asking for a sanitizer that cannot be linked is a FATAL_ERROR, never a
# downgrade. libasan and libubsan ship as separate packages on Debian and
# Ubuntu and are commonly missing even where gcc is present - the compiler will
# happily accept -fsanitize=address and then fail at link, or worse, a
# configure-time probe that only checked compilation would pass and leave the
# operator believing the build was sanitized. Failing loudly is the only safe
# behaviour, because the whole point of this build is that its passing ctest
# line means something stronger than the ordinary one.
#
# Second: -fno-sanitize-recover=undefined. UBSAN's default is to print a
# diagnostic to stderr and keep going. The process then exits 0, ctest records
# a pass, and genuine undefined behaviour scrolls past in output nobody reads.
# Aborting on the first finding converts UB into a test failure.
#
# ASAN is not given halt_on_error explicitly: it aborts on first error by
# default, which is already the behaviour wanted here.
set(KHZ_SANITIZER_FLAGS "")

if(KHZ_ENABLE_ASAN)
    list(APPEND KHZ_SANITIZER_FLAGS -fsanitize=address)
endif()

if(KHZ_ENABLE_UBSAN)
    list(APPEND KHZ_SANITIZER_FLAGS -fsanitize=undefined -fno-sanitize-recover=undefined)
endif()

if(KHZ_SANITIZER_FLAGS)
    if(NOT CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
        message(FATAL_ERROR
            "KHZ_ENABLE_ASAN or KHZ_ENABLE_UBSAN was requested but the "
            "sanitizer flags are GCC and Clang specific and this compiler is "
            "${CMAKE_C_COMPILER_ID}. Refusing to configure a build that would "
            "report passing tests without a sanitizer attached.")
    endif()

    # The probe links as well as compiles. Checking compilation alone would pass
    # on a machine with gcc but without the sanitizer runtime, which is exactly
    # the case this needs to catch.
    set(CMAKE_REQUIRED_FLAGS ${KHZ_SANITIZER_FLAGS})
    set(CMAKE_REQUIRED_LINK_OPTIONS ${KHZ_SANITIZER_FLAGS})
    check_c_compiler_flag("" KHZ_SANITIZERS_LINK)
    unset(CMAKE_REQUIRED_FLAGS)
    unset(CMAKE_REQUIRED_LINK_OPTIONS)

    if(NOT KHZ_SANITIZERS_LINK)
        message(FATAL_ERROR
            "Sanitizers were requested but a test program could not be linked "
            "with ${KHZ_SANITIZER_FLAGS}. The runtime libraries are packaged "
            "separately from the compiler; on Debian and Ubuntu install "
            "libasan and libubsan (for example: sudo apt install -y libasan8 "
            "libubsan1), or configure without -DKHZ_ENABLE_ASAN=ON and "
            "-DKHZ_ENABLE_UBSAN=ON. This is a hard error on purpose: silently "
            "producing an unsanitized build would make a passing ctest run "
            "look like evidence of memory safety when nothing was checked.")
    endif()

    # -g and a kept frame pointer are what turn a sanitizer report from an
    # address into a file and line. Without them the diagnostic still fires but
    # is far harder to act on.
    add_compile_options(${KHZ_SANITIZER_FLAGS} -g -fno-omit-frame-pointer)
    add_link_options(${KHZ_SANITIZER_FLAGS})

    message(STATUS "khz_sheet: sanitizers active: ${KHZ_SANITIZER_FLAGS}")

    # -O3 does not disable sanitizers, but inlining folds frames together and
    # makes reports harder to read. Worth a note rather than overriding a
    # setting the operator chose deliberately.
    if(CMAKE_BUILD_TYPE STREQUAL "Release")
        message(STATUS
            "khz_sheet: sanitizers are active in a Release build; consider "
            "-DCMAKE_BUILD_TYPE=Debug for readable stack traces")
    endif()
endif()

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

    # A sanitized shared library is loadable by a non-sanitized host process
    # such as `dotnet`, but only with the runtime preloaded, and ASAN's
    # allocator interception is unreliable in that configuration. Worth saying
    # here rather than leaving it to be discovered from a confusing crash
    # inside the CLR.
    if(KHZ_SANITIZER_FLAGS)
        message(STATUS
            "khz_sheet: libkhz_sheet.so is sanitized; P/Invoke from dotnet "
            "needs LD_PRELOAD of the sanitizer runtime. Use a separate "
            "unsanitized build directory for the C# bindings.")
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

    add_test(NAME khz_selftest COMMAND khz_selftest)
endif()

# End-to-end tests.
#
# The selftest above checks each layer against known answers it carries in its
# own source. This checks the path that actually matters: a compressed workbook
# written by a third party, through the zip parser and the DEFLATE decoder,
# into exact rationals on the proof chain.
#
# The fixture is generated rather than committed, because its DEFLATE streams
# and CRC-32 checksums must come from an implementation this project did not
# write. That is what makes a passing result evidence rather than the code
# agreeing with itself.
#
# python3 is a test-time convenience only. It is not a library dependency, and
# when it is missing the test is skipped with the reason printed at configure
# time rather than failing the build.
if(KHZ_BUILD_TESTS)
    find_program(KHZ_PYTHON NAMES python3 python)

    if(KHZ_PYTHON)
        set(KHZ_FIXTURE "${CMAKE_CURRENT_BINARY_DIR}/khz_fixture.xlsx")
        set(KHZ_FIXTURE_SCRIPT "${CMAKE_CURRENT_SOURCE_DIR}/tests/make_fixture.py")

        add_custom_command(
            OUTPUT "${KHZ_FIXTURE}"
            COMMAND "${KHZ_PYTHON}" "${KHZ_FIXTURE_SCRIPT}" "${KHZ_FIXTURE}"
            DEPENDS "${KHZ_FIXTURE_SCRIPT}"
            COMMENT "Generating deflated xlsx fixture"
            VERBATIM
        )

        add_custom_target(khz_fixture DEPENDS "${KHZ_FIXTURE}")

        add_executable(khz_xlsx_test tests/khz_xlsx_test.c)
        target_link_libraries(khz_xlsx_test PRIVATE khz_sheet)
        add_dependencies(khz_xlsx_test khz_fixture)

        if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
            target_compile_options(khz_xlsx_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        endif()

        add_test(NAME khz_xlsx_test COMMAND khz_xlsx_test "${KHZ_FIXTURE}")

        message(STATUS "khz_sheet: end-to-end xlsx test enabled (${KHZ_PYTHON})")

        # The same fixture, deliberately broken 800 ways. Reuses the generated
        # archive rather than carrying its own, because the point is to start
        # from something valid and damage it: corruption of a correct file
        # reaches code paths that a hand-written invalid file rarely does,
        # since a bad signature in the first four bytes is rejected before any
        # of the interesting parsing happens.
        add_executable(khz_fuzz_test tests/khz_fuzz_test.c)
        target_link_libraries(khz_fuzz_test PRIVATE khz_sheet)
        add_dependencies(khz_fuzz_test khz_fixture)

        if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
            target_compile_options(khz_fuzz_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        endif()

        add_test(NAME khz_fuzz_test COMMAND khz_fuzz_test "${KHZ_FIXTURE}")

        # This test is the one whose result depends most on how the build was
        # configured. Reading a few bytes past a buffer that sits inside a
        # 64 MiB arena touches memory the process legitimately owns, so without
        # ASAN nothing detects it and the test passes. Said at configure time
        # so a green line in an ordinary build is not mistaken for evidence the
        # decoder is memory safe on hostile input.
        if(KHZ_SANITIZER_FLAGS)
            message(STATUS "khz_sheet: malformed-input test enabled (sanitized)")
        else()
            message(STATUS
                "khz_sheet: malformed-input test enabled, but this build has "
                "no sanitizer. It will detect only crashes, not out-of-bounds "
                "reads within the arena. Configure a second build directory "
                "with -DKHZ_ENABLE_ASAN=ON -DKHZ_ENABLE_UBSAN=ON for a "
                "meaningful result.")
        endif()
    else()
        message(STATUS
            "khz_sheet: python3 not found; the end-to-end xlsx test is skipped. "
            "The fixture must be generated by tests/make_fixture.py, which "
            "needs an interpreter. The library itself does not.")
    endif()
endif()
