import re, glob, sys
import xml.etree.ElementTree as ET

NS_P = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
NS_X = '{http://schemas.microsoft.com/winfx/2006/xaml}'

def tag(el):
    return el.tag.replace(NS_P, '').replace(NS_X, '').split(':')[-1]

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
        a = attr.replace(NS_P, '').replace(NS_X, '').split(':')[-1]
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


def walk2(el, parent_tag, fname, struct_out):
    check_structure(el, parent_tag, fname, struct_out)
    for c in el:
        if isinstance(c.tag, str):
            walk2(c, tag(el), fname, struct_out)


def main():
    files = [x for x in glob.glob('**/*.xaml', recursive=True) if not x.startswith('obj/')]
    kt = collect_keytypes(files)

    type_out, struct_out, parse_fail = [], [], []
    trees = {}
    for f in files:
        try:
            trees[f] = ET.parse(f).getroot()
        except ET.ParseError as e:
            parse_fail.append((f, str(e)))

    for f, root in trees.items():
        walk(root, None, f, type_out, kt)
        walk2(root, None, f, struct_out)

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

    return 1 if (parse_fail or type_out or struct_out) else 0


if __name__ == '__main__':
    sys.exit(main())
