import re, glob, sys
import xml.etree.ElementTree as ET

NS_P = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
NS_X = '{http://schemas.microsoft.com/winfx/2006/xaml}'

def tag(el):
    # 自定义命名空间（xmlns:ctl="clr-namespace:..."）解析出来是
    # "{clr-namespace:ZenForge.Controls}SearchBox" —— 必须连花括号一起剥掉，
    # 否则前面的 "xxx}" 会留在结果里，名称比对永远不命中。
    return re.sub(r'^\{[^}]*\}', '', el.tag).split(':')[-1]

def collect_keytypes(files):
    kt = {}
    for f in files:
        s = open(f, encoding='utf-8-sig').read()
        for m in re.finditer(r'<([\w:.]+)\s+[^>]*?x:Key="([^"]+)"', s):
            kt.setdefault(m.group(2), set()).add(m.group(1))
    return kt

BRUSH   = {'SolidColorBrush','LinearGradientBrush','RadialGradientBrush','Brush','ImageBrush'}
STYLE   = {'Style'}
THICK   = {'Thickness'}
NUM     = {'Double','Int32','Single'}
GRIDLEN = {'GridLength'}
FONT    = {'FontFamily'}
CORNER  = {'CornerRadius'}

# 属性名 -> 允许类型（默认语义，即 FrameworkElement 上的属性）
ALLOW = {
    'Background': BRUSH, 'Foreground': BRUSH, 'BorderBrush': BRUSH,
    'Fill': BRUSH, 'Stroke': BRUSH,
    'Style': STYLE, 'BasedOn': STYLE,
    'Margin': THICK, 'Padding': THICK,
    'FontFamily': FONT,
    'Width': NUM, 'Height': NUM, 'MinWidth': NUM, 'MinHeight': NUM,
    'MaxWidth': NUM, 'MaxHeight': NUM, 'FontSize': NUM,
    'StrokeThickness': NUM, 'BlurRadius': NUM, 'Opacity': NUM,
    'CornerRadius': CORNER,
}

# 父元素决定的覆盖语义：ColumnDefinition.Width / RowDefinition.Height 要 GridLength
PARENT_OVERRIDE = {
    ('ColumnDefinition', 'Width'):  GRIDLEN,
    ('RowDefinition',    'Height'): GRIDLEN,
}

def norm(t):
    return t.split(':')[-1]

def walk(el, parent_tag, fname, out, kt):
    t = tag(el)
    for attr, val in el.attrib.items():
        a = re.sub(r'^\{[^}]*\}', '', attr).split(':')[-1]
        m = re.fullmatch(r'\{(?:Static|Dynamic)Resource\s+([^}]+)\}', val.strip())
        if not m:
            continue
        key = m.group(1).strip()
        allowed = PARENT_OVERRIDE.get((t, a)) or ALLOW.get(a)
        if allowed is None:
            continue
        ktv = kt.get(key)
        if not ktv:
            continue
        if not ({norm(x) for x in ktv} & allowed):
            out.append((fname, t, a, key, sorted(ktv), sorted(allowed)))
    for c in el:
        if isinstance(c.tag, str):
            walk(c, t, fname, out, kt)

def check_structure(el, parent_tag, fname, out):
    """容器与属性/子元素的匹配检查。

    这三条都只在运行时才炸，编译期 0 警告：
      · StackPanel 里写 Grid.ColumnDefinitions -> InvalidCastException，整页加载失败
      · 父级不是 Grid 却写 Grid.Row / Grid.Column -> 附加属性被忽略，布局静默错位
      · Border 多个子元素 -> MC3089
    """
    t = tag(el)

    if t != 'Grid':
        for prop in ('ColumnDefinitions', 'RowDefinitions'):
            if el.find(NS_P + 'Grid.' + prop) is not None:
                out.append((fname, t, f'<{t}> 内出现 Grid.{prop}（该容器没有这个属性）'))

    if t in ('Border', 'Viewbox', 'Decorator'):
        kids = [c for c in el if isinstance(c.tag, str) and '.' not in tag(c)]
        if t == 'Border' and len(kids) > 1:
            out.append((fname, t, f'Border 有 {len(kids)} 个子元素（只允许 1 个）'))

    if t == 'TextBlock' and ('Padding' in el.attrib or
                             el.attrib.get(NS_P + 'Padding') is not None):
        out.append((fname, t, 'TextBlock 没有 Padding 属性'))

    for attr in ('Grid.Row', 'Grid.Column', 'Grid.RowSpan', 'Grid.ColumnSpan'):
        if el.attrib.get(NS_P + attr) is not None and parent_tag != 'Grid':
            out.append((fname, t, f'写了 {attr} 但父级是 <{parent_tag}>，该值会被忽略'))


# ============================================================
# [4] 事件与属性名是否存在于控件上
# ============================================================
# 这一条是为「SearchBox 从 TextBox 换成自定义控件」那次事故加的：
# XAML 里仍然写着 GotFocus="SearchBox_GotFocus"、代码里也仍然实现着
# private void SearchBox_GotFocus(...)，编译期 0 报错；
# 运行期才在 FindName/模板套用阶段抛「找不到名为 TextChanged 的事件」，
# 或者事件根本不触发（占位符永远不消失）。
#
# 判据只能覆盖本项目自己的控件（SearchBox / ToastHost）：它们的成员是已知的，
# 而 WPF 内置控件的成员表写不全，一写就会误报。
KNOWN_CONTROLS = {
    'SearchBox': {
        'props': {'Text', 'Placeholder', 'Height', 'Width', 'Margin',
                  'VerticalAlignment', 'HorizontalAlignment', 'Style', 'Name',
                  'Grid.Row', 'Grid.Column', 'Grid.RowSpan', 'Grid.ColumnSpan',
                  'Background', 'Foreground', 'FontSize', 'FontFamily', 'Cursor',
                  'ToolTip', 'IsEnabled', 'Visibility', 'TabIndex', 'MinWidth',
                  'MaxWidth', 'MinHeight', 'MaxHeight', 'Panel.ZIndex'},
        'events': {'TextChanged', 'InputKeyDown', 'GotFocus', 'LostFocus'},
    },
}

def check_events(root, fname, out):
    for el in root.iter():
        if not isinstance(el.tag, str):
            continue
        t = tag(el)
        if t not in KNOWN_CONTROLS:
            continue
        spec = KNOWN_CONTROLS[t]
        for attr, val in el.attrib.items():
            if attr.startswith(NS_P) or attr.startswith('{'):
                continue
            if attr in spec['props']:
                continue
            # 事件 attribute 的特征：值是一个 C# 方法名（Page_Loaded / OnChanged）
            # attribute 名含下划线的一定是事件；否则只有「值是可调用标识符」
            # 且名字不以常见属性前缀开头时才可能是事件 —— 这样
            # GotFocus="X_Y"（真事件、名字未登记）与 FontWieght="Bold"（打字错误）
            # 报出来的措辞不会互相误导。
            looks_like_event = (attr in spec['events']) or ('_' in attr)
            if looks_like_event:
                if attr not in spec['events']:
                    out.append((fname, t, f'{t} 上没有事件 {attr}（处理函数 {val}）'))
            elif attr not in spec['props']:
                # 值为标识符（PascalCase）说明作者以为这是事件，措辞按事件报，
                # 否则真的是属性名写错
                kind = '事件（或属性）' if re.fullmatch(r'[A-Z][A-Za-z0-9_]*', val) else '属性'
                out.append((fname, t, f'{t} 上没有{kind} {attr}'))

def walk2(el, parent_tag, fname, struct_out):
    check_structure(el, parent_tag, fname, struct_out)
    for c in el:
        if isinstance(c.tag, str):
            walk2(c, tag(el), fname, struct_out)


# ============================================================
# [5] 模板内引用 StaticResource —— 1.0.0-can9 的整机卡死就是这条
# ============================================================
# ControlTemplate / DataTemplate 的内容是在**模板被解析的那一刻**求值的，
# 而 StaticResource 按元素树向上查找。模板里的元素此刻还没有父级，
# 查找链只走到模板本身，永远到不了 Application.Resources。于是：
#
#     XamlParseException: 无法找到名为"AppTextBoxStyle"的资源
#
# 更糟的是它**只在控件第一次真正套模板时**才炸（ApplyTemplate → Measure），
# 编译期 0 警告、启动时一声不响 —— 用户点开进程页的瞬间才整页白掉。
#
# 判据：模板内出现的 StaticResource 键，只允许是本文件自己定义的
#      （模板内的 <Template.Resources> / 同字典顶层 x:Key），
#      以及框架内置键（求值后带 "+" 或命名空间前缀的，本工程定义不了）。
#
# 例外（**合法写法，不算问题**）：挂在外部容器上的模板。xx.ItemTemplate /
# xx.ContentTemplate / xx.CellTemplate 的内容由容器在**已有元素树**里实例化
# （ItemsControl 的视觉子树、ContentPresenter），StaticResource 能沿树一路
# 走到 App.Resources。项目里 LogPage / ProcessBindingPage / MaskManagerWindow
# 的那些 DataTemplate 都是这种，一次都没崩过 —— 把它们算进来会造成 20 条误报，
# 把真问题淹掉。
#
# ControlTemplate 没有这个例外：WPF 在首次 ApplyTemplate 时解析它，
# 此刻元素树根本没搭好，找不到就是找不到。
#
# 注意只查 StaticResource：DynamicResource 是运行期查字典，能走到
# Application.Resources，不受这个限制。也不要为了「保险」把静态引用全改成
# DynamicResource —— 样式类对象每帧重新查字典会重建模板，产生抖动。

TEMPLATE_TAGS = {'ControlTemplate', 'DataTemplate', 'ItemsPanelTemplate'}

# 模板的 DataTemplate 外层命中这些元素时，模板内容其实挂在**元素树的外部容器**上
# （ItemsPanel 是 ItemsControl 的视觉子树、ContentTemplate 是 ContentPresenter 的），
# StaticResource 能沿元素树走到 App.Resources —— 这正是 TuningPage.xaml /
# MaskManagerWindow.xaml 里那份写法合法的原因。ControlTemplate 没有这种例外：
# WPF 在首次 ApplyTemplate 时解析它，此时元素树根本没搭好。
EXTERNAL_CONTAINER = {'ItemsPanel', 'ItemsControl.ItemsPanel'}


def parent_tag_map(root):
    """元素 -> 父元素标签（ElementTree 没有 parent 指针）。"""
    parents = {}
    for parent in root.iter():
        if not isinstance(parent.tag, str):
            continue
        for child in parent:
            if isinstance(child.tag, str):
                parents[child] = tag(parent)
    return parents


def sibling_keys(el):
    """模板内可见的键：自己这一层（<Style>/<ControlTemplate>）及其子孙里定义的。"""
    keys = set()
    for node in el.iter():
        if not isinstance(node.tag, str):
            continue
        k = node.attrib.get(NS_X + 'Key')
        if k:
            keys.add(k)
    return keys


def check_template_staticresource(root, fname, out, parents=None):
    """root 是一个模板节点（ControlTemplate / DataTemplate / ItemsPanelTemplate）。"""
    for el in root.iter():
        if not isinstance(el.tag, str) or tag(el) not in TEMPLATE_TAGS:
            continue

        # 挂在外部容器上时跳过（见文件上方「例外」的说明）
        if parents is not None and tag(el) in ('DataTemplate', 'ItemsPanelTemplate'):
            outer = parents.get(el) or ''
            # xx.ItemTemplate / xx.CellTemplate / xx.ContentTemplate 都是「外部容器」
            if outer.split('.')[-1] in ('ItemTemplate', 'CellTemplate', 'ContentTemplate',
                                        'ItemContainerStyle', 'ContentTemplateSelector'):
                continue

        local = sibling_keys(el)          # 模板自身子树里定义的键
        # 模板外围（同字典顶层）定义的键
        enclosing = {n.attrib.get(NS_X + 'Key') for n in root.iter()
                     if isinstance(n.tag, str) and n.attrib.get(NS_X + 'Key')}

        for node in el.iter():
            if not isinstance(node.tag, str):
                continue
            for attr, val in node.attrib.items():
                m = re.fullmatch(r'\{StaticResource\s+([^}]+)\}', val.strip())
                if not m:
                    continue
                key = m.group(1).strip()
                # 框架内置键（{x:Static SystemColors.WindowBrushKey} 求值后是
                # "SystemColors+WindowBrushKey"，带命名空间或加号），本工程定义不了。
                # 只认这一种，不能把「带点号的键」一刀切跳过 —— 本项目大量
                # 资源键就叫 Text.MetaBrush / Icon.Width，一刀切会把真问题放过去。
                if '+' in key or ':' in key:
                    continue
                if key in local:
                    continue
                scope = '同字典顶层' if key in enclosing else '本工程其它字典'
                out.append((fname, tag(node), key, scope))


def walk3(root, fname, sr_out):
    parents = parent_tag_map(root)
    for node in root.iter():
        if isinstance(node.tag, str) and tag(node) in TEMPLATE_TAGS:
            check_template_staticresource(node, fname, sr_out, parents)


def main():
    files = [x for x in glob.glob('**/*.xaml', recursive=True) if not x.startswith('obj/')]
    kt = collect_keytypes(files)

    type_out, struct_out, parse_fail, ctl_out, sr_out = [], [], [], [], []
    trees = {}
    for f in files:
        try:
            trees[f] = ET.parse(f).getroot()
        except ET.ParseError as e:
            parse_fail.append((f, str(e)))

    for f, root in trees.items():
        walk(root, None, f, type_out, kt)
        walk2(root, None, f, struct_out)
        check_events(root, f, ctl_out)
        walk3(root, f, sr_out)

    print(f'[1] XML 解析失败：{len(parse_fail)}')
    for f, e in parse_fail:
        print(f'    {f}: {e}')

    print(f'[2] 资源类型与属性类型不符：{len(type_out)}')
    for o in type_out:
        print(f'    {o[0]}  在 <{o[1]}> 上设置 {o[2]} <- {o[3]}')
        print(f'        资源定义类型 {o[4]}，该属性接受 {o[5]}')

    print(f'[3] 容器结构问题：{len(struct_out)}')
    for o in struct_out:
        print(f'    {o[0]}  <{o[1]}>  {o[2]}')

    print(f'[4] 自定义控件上的属性/事件不存在：{len(ctl_out)}')
    for o in ctl_out:
        print(f'    {o[0]}  <{o[1]}>  {o[2]}')

    print(f'[5] 模板内引用了模板外看不见的 StaticResource：{len(sr_out)}')
    for f, t, key, scope in sr_out:
        print(f'    {f}  <{t}>  StaticResource "{key}"（该键定义在{scope}，')
        print(f'        模板解析时查找链到不了，运行期首次套模板即抛 XamlParseException）')
        print(f'        改法：内联写出，或改用 DynamicResource')

    return 1 if (parse_fail or type_out or struct_out or ctl_out or sr_out) else 0


if __name__ == '__main__':
    sys.exit(main())
