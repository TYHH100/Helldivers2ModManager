您需要帮助? 那就来对了.  
管理器使用的时候,您可能会遇到的一些常见问题.  
以及面向开发者的说明.

在Github查看该文件 [原文](https://github.com/teutinsa/Helldivers2ModManager/blob/master/Helldivers2ModManager/Resources/Text/Help.md)|[翻译](https://github.com/TYHH100/Helldivers2ModManager/blob/zh-cn_Translations/Helldivers2ModManager/Resources/Text/Help.md).

---
# 对于用户

## 设置
本节将会指导您如何首次设置管理器

首次设置管理器,很简单您只需要Helldivers 2安装位置即可(然后现在更新了自动检测好像也不需要了)

---
## 添加模组
本章节会指导你如何添加模组以及配置功能

添加模组,只需要点击"**添加**"然后选择[Nexus](https://www.nexusmods.com/helldivers2)或其他地方获取到的压缩包.
就会自动添加到模组列表中,部分模组可能支持.
在下拉菜单中的选择预设变体.
点击"**编辑**"可以进行更加详细的设置.

点击"**编辑**"后您可以看到模组包含的独立组件列表,可以自由的启用或禁用组件
如果模组还提供了组件的变体也可以进行选择

---
## 常见问题
本节将说明可能遇到的常见问题

1 模组没有生效 
   - 请先查看作者说明是否需要一些额外操作,其次检查模组文件是否完整
   - 对于模型音效模组.模型模组需要有对应的东西,音效模组您需要有对应触发音效的条件


2 游戏打不开了
   - 由于模组(有些不一定)和游戏是不断在更新的,可能是会产生一些兼容性问题
   - 当然大多数情况都是游戏更新后一些模组无法使用,您需要去下载模组地方的看看作者最新的说明
   - 建议先逐步排查,把那些失效的模组禁用
   - 确保游戏能够正常启动和游玩

3 排序
   - 排序,只需鼠标长按该模组然后拖动即可

---
# 对于开发者
本章节模组创作者
将说明如何适配模组管理器并提升用户体验

大多数模组不需要额外配置就可以被管理器识别,
管理器通过文件结构自动检测模组的结构,但仅支持单层子目录.
但是如果文件结构很复杂,比如多层嵌套,管理器正常显示但是可能无法正确启用


## 清单(manifest.json)
可优化模组在管理器中的显示效果.该文件需置于模组根目录

### 基础清单
适用于无自定义选项的简单模组,示例如下:
```
└┬ 您的模组
 ├── abcdefghijklmnopq.patch_0
 ├── abcdefghijklmnopq.patch_0.stream
 ├── abcdefghijklmnopq.patch_0.gpu_resources
 └── manifest.json
```
```json
//manifest.json
{
    "Version": 1,
    "Guid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "Name": "模组名称",
    "Description": "模组描述"
}
```
说明:
- `Version` : 必须设置为'1'表示用最新的清单格式,所以这个不能作为模组的版本.
- `Guid` : 全局唯一标识,用于区分不同的模组.可以通过[在线网站](https://www.uuidgenerator.net/guid)生成.
- `Name` : 您的模组名字.
- `Description` : 您的模组描述.

在上文的基础上,添加上模组的图标:
```json
{
    "Version": 1,
    "Guid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "Name": "模组名称",
    "Description": "模组描述",
    "IconPath": "icon.png"
}
```
说明:
- `IconPath` : 模组图标(图片文件跟清单一起).

### 高级清单
The new manifest allows for mods to have individual components.
Let's say you have a mod that provides two armors and one has a helmet with two variants.
An example manifest for that scenario would look like this:
```json
{
    "Version": 1,
    "Guid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "Name": "Your mod name here",
    "Description": "Your mod description here",
    "Options": [
        {
            "Name": "Armor 1",
            "Description": "Armor 1 description",
            "Include": [
                "Armor 1"
            ]
        },
        {
            "Name": "Armor 2",
            "Description": "Armor 2 description",
            "Include": [
                "Armor 2"
            ],
            "SubOptions": [
                {
                    "Name": "Helmet variant A",
                    "Description": "Helmet variant A description",
                    "Include": [
                        "Armor 2/Helemt A"
                    ]
                },
                {
                    "Name": "Helmet variant B",
                    "Description": "Helmet variant B description",
                    "Include": [
                        "Armor 2/Helemt B"
                    ]
                }
            ]
        }
    ]
}
```
- `Options` : A list of objects describing togglable components of your mod.
- `SubOptions` : A list of objects describing sub-options for an option were you have
  to choose one.
- `Include` : A list of relative paths to folders containing the appropriate
  patch files for each option respectively.

Everything else should be self explanatory. But here is what the folder structure would
look like, as described by the manifest.
```
└┬ My Armor Mod
 ├── manifest.json
 ├─┬ Armor 1
 │ ├── abcdefghijklmnopq.patch_0
 │ ├── abcdefghijklmnopq.patch_0.stream
 │ └── abcdefghijklmnopq.patch_0.gpu_resources
 └─┬ Armor 2
   ├── abcdefghijklmnopq.patch_0
   ├── abcdefghijklmnopq.patch_0.stream
   ├── abcdefghijklmnopq.patch_0.gpu_resources
   ├─┬ Helmet A
   │ ├── abcdefghijklmnopq.patch_0
   │ ├── abcdefghijklmnopq.patch_0.stream
   │ └── abcdefghijklmnopq.patch_0.gpu_resources
   └─┬ Helmet B
     ├── abcdefghijklmnopq.patch_0
     ├── abcdefghijklmnopq.patch_0.stream
     └── abcdefghijklmnopq.patch_0.gpu_resources
```

### Legacy manifest
The now so called legacy manifest is first manifest used by the manager.
It does not need to be discussed a lot as it's only here for backwards compatibility.
```json
{
    "Guid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "Name": "Your mod name here",
    "Description": "Your mod description here",
    "IconPath": "icon.png",
    "Options": [
        "Option A",
        "Option B"
    ]
}
```
Explanation:
- `Guid` : This is called a global unique identifier. It's used by the manager
  under the hood to tell you mod apart from others.
  You can generate one [here](https://www.uuidgenerator.net/guid).
- `Name` : This is the name of your mod.
- `Description` : This is a short description of your mod.
- `IconPath` : This is a path to an image to use as an icon for your mod.
  The path is relative to the manifest.
- `Options` : This is a list of folder names that each contain patch files to use
  as variants.