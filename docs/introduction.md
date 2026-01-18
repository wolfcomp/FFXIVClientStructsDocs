# Introduction
Here you will find knowledge bits to help reverse engineer the game through various means.

## Script maintainers
| Application Name | Script Maintainer                                                            |
| ---------------- | ---------------------------------------------------------------------------- |
| IDA              | <img id="img-wildwolf" width="24" /> [WildWolf](https://github.com/wolfcomp) |
| Ghidra           | <img id="img-pohky" width="24" /> [Pohky](https://github.com/pohky)          |
| Binja            | <img id="img-notnite" width="24" /> [NotNite](https://github.com/NotNite)    |

## IDA Versions
- IDA 7 :white_check_mark:
- IDA 8 :white_check_mark:
- IDA 9 :question: (*incomplete and still being worked on by <img id="img-caitlyn" width="24" />[Caitlyn](https://github.com/caitlyn-gg)*)

> [!IMPORTANT]
> Full padding will take days to complete on IDA 9


<script>
    var users = {
        "wildwolf": "wolfcomp",
        "pohky": "pohky",
        "notnite": "notnite",
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