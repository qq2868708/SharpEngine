cmake_minimum_required(VERSION 3.24)
project(SharpEngineUiDependencies LANGUAGES C CXX)
option(SHARPENGINE_UI_DEBUG_OPTIMIZATIONS
    "Optimize UI dependencies in Debug while retaining symbols and Debug ABI" ON)
if(MSVC AND SHARPENGINE_UI_DEBUG_OPTIMIZATIONS)
    # UI layout runs on every interactive resize, including in a Debug editor.
    # Keep /Zi, the Debug CRT, assertions, and checked STL iterators. /RTC is
    # incompatible with optimization; dependency source debugging can opt out.
    foreach(Flags CMAKE_C_FLAGS_DEBUG CMAKE_CXX_FLAGS_DEBUG)
        string(REGEX REPLACE "/(Od|Ob0|RTC[1su]+)" "" ${Flags} "${${Flags}}")
        string(APPEND ${Flags} " /O2 /Ob2")
    endforeach()
endif()
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
add_subdirectory(../FreeType FreeType)
add_library(Freetype::Freetype ALIAS freetype)
set(RMLUI_SAMPLES OFF CACHE BOOL "" FORCE)
set(RMLUI_PRECOMPILED_HEADERS ON CACHE BOOL "" FORCE)
add_subdirectory(../RmlUi RmlUi)
