# Introduction
Here you will find knowledge bits to help reverse engineer the game through various means.

## Script maintainers
| Application Name | Script Maintainer                                                            |
| ---------------- | ---------------------------------------------------------------------------- |
| IDA              | <img id="img-wildwolf" width="24" /> [WildWolf](https://github.com/wolfcomp) |
| Ghidra           | <img id="img-pohky" width="24" /> [Pohky](https://github.com/pohky)          |
| Binja            | <img id="img-notnite" width="24" /> [NotNite](https://github.com/NotNite)    |

## IDA Versions
- IDA 9 :x: [reason here](https://github.com/aers/FFXIVClientStructs/issues/1235)
- IDA 8 :white_check_mark:
- IDA 7 :white_check_mark:


<script>
(function(){
    fetch("https://api.github.com/users/wolfcomp").then(res => res.json()).then(ret => {
        document.querySelector("#img-wildwolf").src = ret.avatar_url;
    });
    fetch("https://api.github.com/users/pohky").then(res => res.json()).then(ret => {
        document.querySelector("#img-pohky").src = ret.avatar_url;
    });
    fetch("https://api.github.com/users/notnite").then(res => res.json()).then(ret => {
        document.querySelector("#img-notnite").src = ret.avatar_url;
    });
})()
</script>