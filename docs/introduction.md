# Introduction
Here you will find knowledge bits to help reverse engineer the game through various means.

## Script maintainers
| Application Name | Script Maintainer                                                            |
| ---------------- | ---------------------------------------------------------------------------- |
| IDA              | <img id="img-wildwolf" width="24" /> [WildWolf](https://github.com/wolfcomp) |
| Ghidra           | <img id="img-pohky" width="24" /> [Pohky](https://github.com/pohky)          |

### Other related script people
<img id="img-caitlyn" width="24" />[Caitlyn](https://github.com/caitlyn-gg) for their initial IDA 9 implementation.

## IDA Versions
- IDA 7 :white_check_mark: (*Being phased out with Evercold*)
- IDA 8 :white_check_mark: (*Being phased out with Evercold*)
- IDA 9 :white_check_mark: (*Use branch 8.0-work for optimal performance*)

> [!IMPORTANT]
> With the new script reimports will take longer on an existing import base but is less than 10 minutes on a fresh database

### Why does bitfields not get imported with IDA?
This is due to internal limitations with the API surface used to create structs within the Python API and type library. Bellow are the pros and cons of the different API calls that can be made in order to create a struct, and currently we use the first option. With the 8.0-work it will switch over to using the last option still bitfields are not supported there due to an issue in IDAs type library.

| Api Call                            | Pro                                     | Cons                                                                             |
| ----------------------------------- | --------------------------------------- | -------------------------------------------------------------------------------- |
| ida_struct.add_struc_member (< 8.3) | Allows for setting at a specific offset | Very limiting about what can be set due to relliance on type id                  |
| ida_typeinf.parse_decl              | Allows for parsing of C files           | Doesn't allow for C++ chracter types where we use template characters from C++   |
| ida_srclang.parse_decls_with_parser | Allows for parsing of C++ headers       | Requires a full export of Virtual Table list as their defined in the C++ headers |

<script>
    var users = {
        "wildwolf": "wolfcomp",
        "pohky": "pohky",
        "caitlyn": "caitlyn-gg",
    };
(function(){
    Object.keys(users).forEach(v => {
        fetch(`https://api.github.com/users/${users[v]}`).then(res => res.json()).then(ret => {
            var elem = document.querySelector(`#img-${v}`);
            if (!!elem)
                elem.src = ret.avatar_url;
        });
    });
})()
</script>