#ifndef KHZ_EXPORT_H
#define KHZ_EXPORT_H

#if defined(KHZ_STATIC)
#  define KHZ_API
#elif defined(_WIN32)
#  if defined(KHZ_BUILDING_LIBRARY)
#    define KHZ_API __declspec(dllexport)
#  else
#    define KHZ_API __declspec(dllimport)
#  endif
#elif defined(__GNUC__) || defined(__clang__)
#  define KHZ_API __attribute__((visibility("default")))
#else
#  define KHZ_API
#endif

#endif
