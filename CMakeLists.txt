cmake_minimum_required(VERSION 3.16)

project(khz_sheet C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# Distributed/default builds use the baseline ISA. AVX2 is an explicit build
# choice: when enabled only khz_simd.c receives -mavx2, so unrelated code is
# never auto-vectorised past the baseline CPU contract. This is compile-time
# selection, not runtime dispatch; an AVX2 build must run on an AVX2 CPU.
option(KHZ_ENABLE_AVX2 "Compile khz_simd.c with AVX2 (requires an AVX2 CPU at runtime)" OFF)
option(KHZ_ENABLE_LEDGER "Compile the SQLite proof-chain ledger" ON)
option(KHZ_BUILD_SELFTEST "Build the self-test executable" ON)
option(KHZ_BUILD_TESTS "Build the end-to-end and regression tests" ON)
option(KHZ_BUILD_SHARED "Build the shared library used by the C# bindings" ON)
option(KHZ_ENABLE_ASAN "Build with AddressSanitizer" OFF)
option(KHZ_ENABLE_UBSAN "Build with UndefinedBehaviorSanitizer" OFF)

enable_testing()
include(CheckCCompilerFlag)

set(KHZ_SANITIZER_FLAGS "")
if(KHZ_ENABLE_ASAN)
    list(APPEND KHZ_SANITIZER_FLAGS -fsanitize=address)
endif()
if(KHZ_ENABLE_UBSAN)
    list(APPEND KHZ_SANITIZER_FLAGS -fsanitize=undefined -fno-sanitize-recover=undefined)
endif()

if(KHZ_SANITIZER_FLAGS)
    if(NOT CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
        message(FATAL_ERROR "Requested sanitizers are supported here only with GCC/Clang")
    endif()

    set(CMAKE_REQUIRED_FLAGS ${KHZ_SANITIZER_FLAGS})
    set(CMAKE_REQUIRED_LINK_OPTIONS ${KHZ_SANITIZER_FLAGS})
    check_c_compiler_flag("" KHZ_SANITIZERS_LINK)
    unset(CMAKE_REQUIRED_FLAGS)
    unset(CMAKE_REQUIRED_LINK_OPTIONS)

    if(NOT KHZ_SANITIZERS_LINK)
        message(FATAL_ERROR "Sanitizers requested but their runtime could not be linked")
    endif()

    add_compile_options(${KHZ_SANITIZER_FLAGS} -g -fno-omit-frame-pointer)
    add_link_options(${KHZ_SANITIZER_FLAGS})
    message(STATUS "khz_sheet: sanitizers active: ${KHZ_SANITIZER_FLAGS}")
endif()

set(KHZ_SOURCES
    src/khz_arena.c
    src/khz_hash.c
    src/khz_rational.c
    src/khz_cell.c
    src/khz_grid.c
    src/khz_simd.c
    src/khz_sheet.c
    src/khz_sheet_selftest.c
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

check_c_compiler_flag("-mavx2" KHZ_COMPILER_ACCEPTS_MAVX2)
if(KHZ_ENABLE_AVX2)
    if(NOT KHZ_COMPILER_ACCEPTS_MAVX2)
        message(FATAL_ERROR "KHZ_ENABLE_AVX2=ON but this compiler does not accept -mavx2")
    endif()
    set_source_files_properties(src/khz_simd.c PROPERTIES COMPILE_OPTIONS "-mavx2")
    message(STATUS "khz_sheet: AVX2 enabled for src/khz_simd.c only")
else()
    message(STATUS "khz_sheet: baseline ISA build; scalar or architecture baseline SIMD is used")
endif()

set(KHZ_LEDGER_ACTIVE OFF)
if(KHZ_ENABLE_LEDGER)
    find_path(KHZ_SQLITE3_INCLUDE_DIR sqlite3.h)
    find_library(KHZ_SQLITE3_LIBRARY NAMES sqlite3)

    if(NOT KHZ_SQLITE3_INCLUDE_DIR OR NOT KHZ_SQLITE3_LIBRARY)
        message(FATAL_ERROR
            "KHZ_ENABLE_LEDGER=ON but system sqlite3 was not found. Install its development package or configure with -DKHZ_ENABLE_LEDGER=OFF")
    endif()

    set(KHZ_LEDGER_ACTIVE ON)
    target_include_directories(khz_sheet_objects PRIVATE ${KHZ_SQLITE3_INCLUDE_DIR})
    target_compile_definitions(khz_sheet_objects PRIVATE KHZ_WITH_SQLITE=1)
    message(STATUS "khz_sheet: ledger enabled against ${KHZ_SQLITE3_LIBRARY}")
else()
    message(STATUS "khz_sheet: ledger disabled; khz_ledger_* returns ERR_UNSUPPORTED")
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
    set_target_properties(khz_sheet_shared PROPERTIES OUTPUT_NAME khz_sheet)
    target_include_directories(khz_sheet_shared PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    )
    if(KHZ_LEDGER_ACTIVE)
        target_link_libraries(khz_sheet_shared PUBLIC ${KHZ_SQLITE3_LIBRARY})
    endif()
endif()

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

if(KHZ_BUILD_TESTS)
    add_executable(khz_sheet_failure_test tests/khz_sheet_failure_test.c)
    target_link_libraries(khz_sheet_failure_test PRIVATE khz_sheet)
    add_test(NAME khz_sheet_failure_test COMMAND khz_sheet_failure_test)

    add_executable(khz_sheet_proof_tamper_test tests/khz_sheet_proof_tamper_test.c)
    target_link_libraries(khz_sheet_proof_tamper_test PRIVATE khz_sheet)
    add_test(NAME khz_sheet_proof_tamper_test COMMAND khz_sheet_proof_tamper_test)

    add_executable(khz_xlsx_writer_test tests/khz_xlsx_writer_test.c)
    target_link_libraries(khz_xlsx_writer_test PRIVATE khz_sheet)
    add_test(NAME khz_xlsx_writer_test COMMAND khz_xlsx_writer_test)

    if(KHZ_LEDGER_ACTIVE)
        add_executable(khz_ledger_integrity_test tests/khz_ledger_integrity_test.c)
        target_compile_definitions(khz_ledger_integrity_test PRIVATE KHZ_WITH_SQLITE=1)
        target_include_directories(khz_ledger_integrity_test PRIVATE ${KHZ_SQLITE3_INCLUDE_DIR})
        target_link_libraries(khz_ledger_integrity_test PRIVATE khz_sheet ${KHZ_SQLITE3_LIBRARY})
        add_test(NAME khz_ledger_integrity_test COMMAND khz_ledger_integrity_test)
    endif()

    if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
        target_compile_options(khz_sheet_failure_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        target_compile_options(khz_sheet_proof_tamper_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        target_compile_options(khz_xlsx_writer_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        if(KHZ_LEDGER_ACTIVE)
            target_compile_options(khz_ledger_integrity_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        endif()
    endif()

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
        add_test(NAME khz_xlsx_test COMMAND khz_xlsx_test "${KHZ_FIXTURE}")

        add_executable(khz_xlsx_cd_size_test tests/khz_xlsx_cd_size_test.c)
        target_link_libraries(khz_xlsx_cd_size_test PRIVATE khz_sheet)
        add_dependencies(khz_xlsx_cd_size_test khz_fixture)
        add_test(NAME khz_xlsx_cd_size_test COMMAND khz_xlsx_cd_size_test "${KHZ_FIXTURE}")

        add_executable(khz_fuzz_test tests/khz_fuzz_test.c)
        target_link_libraries(khz_fuzz_test PRIVATE khz_sheet)
        add_dependencies(khz_fuzz_test khz_fixture)
        add_test(NAME khz_fuzz_test COMMAND khz_fuzz_test "${KHZ_FIXTURE}")

        if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
            target_compile_options(khz_xlsx_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
            target_compile_options(khz_xlsx_cd_size_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
            target_compile_options(khz_fuzz_test PRIVATE -Wall -Wextra -Wshadow -Wvla)
        endif()
    else()
        message(STATUS "khz_sheet: python3 not found; external deflated-xlsx tests skipped")
    endif()
endif()
